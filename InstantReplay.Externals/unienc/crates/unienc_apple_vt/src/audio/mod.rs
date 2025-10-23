use std::{ffi::c_void, ptr::NonNull};

use anyhow::Result;
use bincode::{Decode, Encode};
use objc2_audio_toolbox::{
    kAudioConverterCompressionMagicCookie, kAudioConverterEncodeBitRate,
    kAudioConverterPropertyMaximumOutputPacketSize, AudioConverterDispose,
    AudioConverterFillComplexBuffer, AudioConverterGetProperty, AudioConverterGetPropertyInfo,
    AudioConverterNew, AudioConverterPropertyID, AudioConverterRef, AudioConverterSetProperty,
};
use objc2_core_audio_types::{
    kAudioFormatFlagIsPacked, kAudioFormatFlagIsSignedInteger, kAudioFormatLinearPCM,
    kAudioFormatMPEG4AAC, AudioBuffer, AudioBufferList, AudioStreamBasicDescription,
    AudioStreamPacketDescription, MPEG4ObjectID,
};
use tokio::sync::mpsc;
use unienc_common::{
    AudioSample, EncodedData, Encoder, EncoderInput, EncoderOutput, UniencDataKind,
};

use crate::OsStatus;

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

    fn get(self) -> anyhow::Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl EncoderInput for AudioToolboxEncoderInput {
    type Data = AudioSample;

    async fn push(&mut self, data: Self::Data) -> anyhow::Result<()> {
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
                let packet = AudioPacket {
                    data: output_buffer_data[packet_desc.mStartOffset as usize
                        ..packet_desc.mStartOffset as usize + packet_desc.mDataByteSize as usize]
                        .to_vec(),
                    timestamp_in_samples: data.timestamp_in_samples,
                    sample_rate: self.sample_rate,
                    magic_cookie: magic_cookie.clone(),
                };
                self.tx.send(packet).await?;
            }

            sample.is_some()
        } {}

        // we need to keep the data until next fill_complex_buffer call
        self.last_data = Some(data);
        Ok(())
    }
}

impl AudioToolboxEncoder {
    pub fn new(options: &impl unienc_common::AudioEncoderOptions) -> anyhow::Result<Self> {
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
            },
            output: AudioToolboxEncoderOutput { rx },
        })
    }
}

impl EncoderOutput for AudioToolboxEncoderOutput {
    type Data = AudioPacket;

    async fn pull(&mut self) -> Result<Option<Self::Data>> {
        Ok(self.rx.recv().await)
    }
}

impl EncodedData for AudioPacket {
    fn timestamp(&self) -> f64 {
        self.timestamp_in_samples as f64 / self.sample_rate as f64
    }

    fn kind(&self) -> UniencDataKind {
        UniencDataKind::Key
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
