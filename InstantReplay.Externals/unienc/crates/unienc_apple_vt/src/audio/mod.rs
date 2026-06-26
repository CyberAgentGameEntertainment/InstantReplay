use std::{ffi::c_void, ptr::NonNull};

use crate::error::{AppleError, OsStatusExt, Result};
use bincode::{Decode, Encode};
use objc2_audio_toolbox::{
    AudioConverterDispose, AudioConverterFillComplexBuffer, AudioConverterGetProperty,
    AudioConverterGetPropertyInfo, AudioConverterNew, AudioConverterPropertyID, AudioConverterRef,
    AudioConverterSetProperty, kAudioConverterCompressionMagicCookie, kAudioConverterEncodeBitRate,
    kAudioConverterPropertyMaximumOutputPacketSize,
};
use objc2_core_audio_types::{
    AudioBuffer, AudioBufferList, AudioStreamBasicDescription, AudioStreamPacketDescription,
    MPEG4ObjectID, kAudioFormatFlagIsPacked, kAudioFormatFlagIsSignedInteger,
    kAudioFormatLinearPCM, kAudioFormatMPEG4AAC,
};
use tokio::sync::mpsc;
use unienc_common::{
    AudioSample, EncodedData, Encoder, EncoderInput, EncoderOutput, UniencSampleKind,
};

pub struct AudioToolboxEncoder {
    input: AudioToolboxEncoderInput,
    output: AudioToolboxEncoderOutput,
}

pub struct AudioToolboxEncoderInput {
    tx: mpsc::Sender<AudioPacket>,
    converter: AudioConverter,
    max_output_packet_size: u32,
    sample_rate: u32,
    last_data: Option<AudioSample>,
    /// Running presentation position (in samples) of the next output packet to emit. Anchored to the
    /// first input buffer's timestamp and advanced by `frames_per_packet` for each emitted packet.
    output_position_in_samples: Option<u64>,
    /// Expected input timestamp (in samples) of the next push, i.e. the previous push's timestamp plus
    /// the number of frames it delivered. Used to detect discontinuities in the input timeline.
    next_input_position: Option<u64>,
    /// Number of samples represented by a single output (AAC) packet. For AAC-LC this is 1024.
    frames_per_packet: u64,
}

unsafe impl Send for AudioToolboxEncoderInput {}

pub struct AudioToolboxEncoderOutput {
    rx: mpsc::Receiver<AudioPacket>,
}

#[derive(Encode, Decode, Clone)]
pub struct AudioPacket {
    pub data: Vec<u8>,
    pub timestamp_in_samples: u64,
    pub sample_rate: u32,
    pub magic_cookie: Vec<u8>,
}

impl Encoder for AudioToolboxEncoder {
    type InputType = AudioToolboxEncoderInput;

    type OutputType = AudioToolboxEncoderOutput;

    fn get(self) -> unienc_common::Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl EncoderInput for AudioToolboxEncoderInput {
    type Data = AudioSample;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        // Keep the encoded audio contiguous in sample-count terms so the muxer (which derives audio timing
        // from the number of encoded samples, not from explicit PTS gaps) keeps audio aligned with video.
        // When the input timeline jumps forward (e.g. dropped or suspended audio that advances
        // `timestamp_in_samples` by more than the number of samples actually delivered), fill the gap with
        // leading silence rather than relying on a PTS jump the muxer may collapse. Backward jumps are
        // ignored by `forward_audio_discontinuity`, so the timeline never regresses.
        let channels = (self.converter.from.mChannelsPerFrame as u64).max(1);
        let frames_in_push = data.data.len() as u64 / channels;
        self.output_position_in_samples
            .get_or_insert(data.timestamp_in_samples);
        let gap = unienc_common::forward_audio_discontinuity(
            self.next_input_position,
            data.timestamp_in_samples,
        );
        self.next_input_position = Some(data.timestamp_in_samples + frames_in_push);

        let data = if gap > 0 {
            let mut padded = Vec::with_capacity(((gap + frames_in_push) * channels) as usize);
            padded.resize((gap * channels) as usize, 0);
            padded.extend_from_slice(&data.data);
            AudioSample {
                data: padded,
                timestamp_in_samples: data.timestamp_in_samples,
            }
        } else {
            data
        };

        let mut output_buffer_data = vec![0; self.max_output_packet_size as usize];

        let max_output_packets = 1;

        let mut packet_descs =
            vec![unsafe { std::mem::zeroed::<AudioStreamPacketDescription>() }; max_output_packets];

        let mut sample = Some(&data);

        while {
            let num_output_packets = self.converter.fill_complex_buffer(
                &mut sample,
                &mut output_buffer_data,
                &mut packet_descs,
            )?;

            let magic_cookie = self
                .converter
                .get_property_raw(kAudioConverterCompressionMagicCookie)?;

            let packet_descs = &packet_descs[..num_output_packets as usize];

            for packet_desc in packet_descs {
                // AudioToolbox can emit multiple output packets from a single input buffer (e.g. when a
                // large batched buffer is pushed while the main thread is stalled by framerate jitter).
                // Assigning every packet the input buffer's timestamp would produce duplicate / overlapping
                // PTS that the muxer (AVAssetWriter) rejects. The encoder consumes a continuous PCM stream,
                // so assign a contiguous per-packet timeline advancing by exactly one packet for each
                // emitted packet. The running position is seeded to the first input timestamp at the start
                // of `push`; forward discontinuities are materialized there as leading silence, so the
                // position simply advances one packet at a time here.
                let timestamp_in_samples = self
                    .output_position_in_samples
                    .expect("output_position_in_samples is initialized at the start of push");

                let packet = AudioPacket {
                    data: output_buffer_data[packet_desc.mStartOffset as usize
                        ..packet_desc.mStartOffset as usize + packet_desc.mDataByteSize as usize]
                        .to_vec(),
                    timestamp_in_samples,
                    sample_rate: self.sample_rate,
                    magic_cookie: magic_cookie.clone(),
                };
                self.tx.send(packet).await.map_err(AppleError::from)?;

                self.output_position_in_samples =
                    Some(timestamp_in_samples + self.frames_per_packet);
            }

            sample.is_some()
        } {}

        // we need to keep the data until next fill_complex_buffer call
        self.last_data = Some(data);
        Ok(())
    }
}

impl AudioToolboxEncoder {
    pub fn new(options: &impl unienc_common::AudioEncoderOptions) -> Result<Self> {
        let mut from = AudioStreamBasicDescription {
            mSampleRate: options.sample_rate() as f64,
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 4,
            mFramesPerPacket: 1,
            mBytesPerFrame: 4,
            mChannelsPerFrame: options.channels(),
            mBitsPerChannel: 16,
            mReserved: 0,
        };

        let mut to = AudioStreamBasicDescription {
            mSampleRate: options.sample_rate() as f64,
            mFormatID: kAudioFormatMPEG4AAC,
            mFormatFlags: MPEG4ObjectID::AAC_LC.0 as u32,
            mBytesPerPacket: 0,
            mFramesPerPacket: 1024,
            mBytesPerFrame: 0,
            mChannelsPerFrame: options.channels(),
            mBitsPerChannel: 0,
            mReserved: 0,
        };

        let converter = AudioConverter::new(&mut from, &mut to)?;

        converter.set_property::<u32>(kAudioConverterEncodeBitRate, &options.bitrate())?;

        let mut max_output_packet_size = 0_u32;
        converter.get_property(
            kAudioConverterPropertyMaximumOutputPacketSize,
            &mut max_output_packet_size,
        )?;

        let (tx, rx) = mpsc::channel(32);

        Ok(Self {
            input: AudioToolboxEncoderInput {
                tx,
                converter,
                max_output_packet_size,
                sample_rate: options.sample_rate(),
                last_data: None,
                output_position_in_samples: None,
                next_input_position: None,
                frames_per_packet: to.mFramesPerPacket as u64,
            },
            output: AudioToolboxEncoderOutput { rx },
        })
    }
}

impl EncoderOutput for AudioToolboxEncoderOutput {
    type Data = AudioPacket;

    async fn pull(&mut self) -> unienc_common::Result<Option<Self::Data>> {
        Ok(self.rx.recv().await)
    }
}

impl EncodedData for AudioPacket {
    fn timestamp(&self) -> f64 {
        self.timestamp_in_samples as f64 / self.sample_rate as f64
    }

    fn kind(&self) -> UniencSampleKind {
        UniencSampleKind::Key
    }

    fn set_timestamp(&mut self, timestamp: f64) {
        self.timestamp_in_samples = (timestamp * self.sample_rate as f64) as u64;
    }
}

struct AudioConverter {
    converter: AudioConverterRef,
    from: AudioStreamBasicDescription,
    to: AudioStreamBasicDescription,
}

impl AudioConverter {
    fn new(
        from: &mut AudioStreamBasicDescription,
        to: &mut AudioStreamBasicDescription,
    ) -> Result<Self> {
        let mut converter: AudioConverterRef = std::ptr::null_mut();
        unsafe {
            AudioConverterNew(
                NonNull::new(from).unwrap(),
                NonNull::new(to).unwrap(),
                NonNull::new(&mut converter).unwrap(),
            )
            .to_result()
        }?;

        Ok(AudioConverter {
            converter,
            from: *from,
            to: *to,
        })
    }

    fn get_property<T: Sized>(
        &self,
        property_id: AudioConverterPropertyID,
        out_data: &mut T,
    ) -> Result<()> {
        let mut size = size_of::<T>() as u32;

        unsafe {
            AudioConverterGetProperty(
                self.converter,
                property_id,
                NonNull::new(&mut size).unwrap(),
                NonNull::new(out_data as *mut _ as *mut c_void).unwrap(),
            )
            .to_result()
        }?;

        Ok(())
    }

    #[allow(dead_code)]
    fn get_property_raw(&self, property_id: AudioConverterPropertyID) -> Result<Vec<u8>> {
        let mut size = 0_u32;
        let mut writable: u8 = 0;
        unsafe {
            AudioConverterGetPropertyInfo(self.converter, property_id, &mut size, &mut writable)
                .to_result()
        }?;

        let mut data = vec![0_u8; size as usize];

        unsafe {
            AudioConverterGetProperty(
                self.converter,
                property_id,
                NonNull::new(&mut size).unwrap(),
                NonNull::new(&mut data[..] as *mut _ as *mut c_void).unwrap(),
            )
            .to_result()?
        };

        Ok(data)
    }

    fn set_property<T: Sized>(
        &self,
        property_id: AudioConverterPropertyID,
        data: &T,
    ) -> Result<()> {
        let size = size_of::<T>() as u32;

        unsafe {
            AudioConverterSetProperty(
                self.converter,
                property_id,
                size,
                NonNull::new(data as *const _ as *mut c_void).unwrap(),
            )
        }
        .to_result()?;

        Ok(())
    }

    fn fill_complex_buffer(
        &self,
        sample: &mut Option<&AudioSample>,
        output_buffer: &mut [u8],
        packet_descs: &mut [AudioStreamPacketDescription],
    ) -> Result<u32> {
        let mut output_buffer_list = AudioBufferList {
            mNumberBuffers: 1,
            mBuffers: [AudioBuffer {
                mNumberChannels: self.to.mChannelsPerFrame,
                mDataByteSize: output_buffer.len() as u32,
                mData: output_buffer.as_ptr() as *mut c_void,
            }],
        };

        let mut input_data_proc =
            |_converter: AudioConverterRef,
             io_number_data_packets: NonNull<u32>,
             io_data: NonNull<AudioBufferList>,
             _out_data_packet_description: *mut *mut AudioStreamPacketDescription|
             -> i32 {
                let data = unsafe { &mut *io_data.as_ptr() };

                let number_data_packets = unsafe { &mut *io_number_data_packets.as_ptr() };

                let Some(sample) = sample.take() else {
                    *number_data_packets = 0;
                    return FillBufferResult::Skip as i32;
                };

                let num_input_packets = sample.data.len() * 2 / self.from.mBytesPerPacket as usize;

                data.mNumberBuffers = 1;
                data.mBuffers[0].mNumberChannels = self.from.mChannelsPerFrame;
                data.mBuffers[0].mDataByteSize = (sample.data.len() * 2) as u32;
                data.mBuffers[0].mData = sample.data.as_ptr() as *mut c_void;
                *number_data_packets = num_input_packets as u32;

                FillBufferResult::Ok as i32
            };

        let mut num_output_packets = packet_descs.len() as u32;

        let ret = core(
            self.converter,
            &mut input_data_proc,
            NonNull::new(&mut num_output_packets).unwrap(),
            NonNull::new(&mut output_buffer_list).unwrap(),
            &mut packet_descs[0],
        );

        if ret != FillBufferResult::Skip as i32 {
            ret.to_result()?;
        }

        return Ok(num_output_packets);

        #[inline(always)]
        fn core<
            T: FnMut(
                AudioConverterRef,
                NonNull<u32>,
                NonNull<AudioBufferList>,
                *mut *mut AudioStreamPacketDescription,
            ) -> i32,
        >(
            in_audio_converter: AudioConverterRef,
            in_input_data_proc: &mut T,
            io_output_data_packet_size: NonNull<u32>,
            out_output_data: NonNull<AudioBufferList>,
            out_packet_description: *mut AudioStreamPacketDescription,
        ) -> i32 {
            unsafe extern "C-unwind" fn input_data<
                T: FnMut(
                    AudioConverterRef,
                    NonNull<u32>,
                    NonNull<AudioBufferList>,
                    *mut *mut AudioStreamPacketDescription,
                ) -> i32,
            >(
                converter: AudioConverterRef,
                io_number_data_packets: NonNull<u32>,
                io_data: NonNull<AudioBufferList>,
                out_data_packet_description: *mut *mut AudioStreamPacketDescription,
                in_user_data: *mut c_void,
            ) -> i32 {
                let input_data_proc = in_user_data as *mut T;
                (unsafe { &mut *input_data_proc })(
                    converter,
                    io_number_data_packets,
                    io_data,
                    out_data_packet_description,
                )
            }

            unsafe {
                AudioConverterFillComplexBuffer(
                    in_audio_converter,
                    Some(input_data::<T>),
                    in_input_data_proc as *mut _ as *mut c_void,
                    io_output_data_packet_size,
                    out_output_data,
                    out_packet_description,
                )
            }
        }
    }
}

#[repr(i32)]
enum FillBufferResult {
    Ok = 0,
    Skip = 1,
}

impl Drop for AudioConverter {
    fn drop(&mut self) {
        unsafe {
            AudioConverterDispose(self.converter);
        }
    }
}
