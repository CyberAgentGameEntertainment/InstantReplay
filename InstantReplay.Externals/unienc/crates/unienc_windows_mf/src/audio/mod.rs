use crate::error::Result;
use bincode::{Decode, Encode};
use tokio::sync::mpsc;
use unienc_common::{
    AudioEncoderOptions, AudioSample, EncodedData, Encoder, EncoderInput, EncoderOutput,
    UniencSampleKind,
};
use windows::Win32::Media::MediaFoundation::*;

use crate::common::*;
use crate::mft::Transform;

pub struct MediaFoundationAudioEncoder {
    transform: Transform,
    output_rx: mpsc::Receiver<UnsafeSend<IMFSample>>,
    sample_rate: u32,
    channels: u32,
}

impl MediaFoundationAudioEncoder {
    pub fn new<V: AudioEncoderOptions>(options: &V) -> Result<Self> {
        let input_type = unsafe {
            let input_type = MFCreateMediaType()?;
            input_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Audio)?;
            input_type.SetGUID(&MF_MT_SUBTYPE, &MFAudioFormat_PCM)?;
            input_type.SetUINT32(&MF_MT_AUDIO_BITS_PER_SAMPLE, 16)?;
            input_type.SetUINT32(&MF_MT_AUDIO_SAMPLES_PER_SECOND, options.sample_rate())?;
            input_type.SetUINT32(&MF_MT_AUDIO_NUM_CHANNELS, options.channels())?;
            input_type
        };

        let output_type = unsafe {
            let output_type = MFCreateMediaType()?;
            output_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Audio)?;
            output_type.SetGUID(&MF_MT_SUBTYPE, &MFAudioFormat_AAC)?;
            output_type.SetUINT32(&MF_MT_AUDIO_BITS_PER_SAMPLE, 16)?;
            output_type.SetUINT32(&MF_MT_AUDIO_SAMPLES_PER_SECOND, options.sample_rate())?;
            output_type.SetUINT32(&MF_MT_AUDIO_NUM_CHANNELS, options.channels())?;
            output_type.SetUINT32(&MF_MT_AUDIO_AVG_BYTES_PER_SECOND, options.bitrate() >> 3)?;
            output_type
        };

        let (transform, output_rx) = Transform::new(
            MFT_CATEGORY_AUDIO_ENCODER,
            MFT_REGISTER_TYPE_INFO {
                guidMajorType: MFMediaType_Audio,
                guidSubtype: MFAudioFormat_PCM,
            },
            MFT_REGISTER_TYPE_INFO {
                guidMajorType: MFMediaType_Audio,
                guidSubtype: MFAudioFormat_AAC,
            },
            input_type,
            output_type,
        )?;

        Ok(Self {
            transform,
            output_rx,
            sample_rate: options.sample_rate(),
            channels: options.channels(),
        })
    }
}

impl Encoder for MediaFoundationAudioEncoder {
    type InputType = AudioEncoderInputImpl;
    type OutputType = AudioEncoderOutputImpl;

    fn get(self) -> unienc_common::Result<(Self::InputType, Self::OutputType)> {
        let media_type = Some(UnsafeSend(self.transform.output_type()?.clone()));
        Ok((
            AudioEncoderInputImpl {
                transform: self.transform,
                sample_rate: self.sample_rate,
                channels: self.channels,
            },
            AudioEncoderOutputImpl {
                receiver: self.output_rx,
                media_type,
            },
        ))
    }
}

pub struct AudioEncoderInputImpl {
    transform: Transform,
    sample_rate: u32,
    channels: u32,
}

pub struct AudioEncoderOutputImpl {
    media_type: Option<UnsafeSend<IMFMediaType>>,
    receiver: mpsc::Receiver<UnsafeSend<IMFSample>>,
}

impl EncoderInput for AudioEncoderInputImpl {
    type Data = AudioSample;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        let sample = UnsafeSend(unsafe { MFCreateSample()? });

        // BGRA to NV12
        {
            let length = (data.data.len() * std::mem::size_of::<i16>()) as u32;
            let buffer = unsafe { MFCreateMemoryBuffer(length)? };

            unsafe { sample.AddBuffer(&buffer)? };

            let mut buffer_ptr: *mut u8 = std::ptr::null_mut();
            unsafe { buffer.Lock(&mut buffer_ptr, None, None)? };

            unsafe {
                std::ptr::copy_nonoverlapping(
                    data.data.as_ptr() as *const u8,
                    buffer_ptr,
                    length as usize,
                );
            }

            unsafe { buffer.SetCurrentLength(length)? }

            unsafe { buffer.Unlock()? };
        }

        unsafe {
            sample.SetSampleTime(
                (data.timestamp_in_samples as f64 / self.sample_rate as f64 * 10_000_000_f64)
                    as i64,
            )?
        };
        unsafe {
            sample.SetSampleDuration(
                ((data.data.len() / self.channels as usize) as f64 / self.sample_rate as f64
                    * 10_000_000_f64) as i64,
            )?
        };
        self.transform.push(sample).await
    }
}

impl EncoderOutput for AudioEncoderOutputImpl {
    type Data = AudioEncodedData;

    async fn pull(&mut self) -> unienc_common::Result<Option<Self::Data>> {
        if let Some(media_type) = self.media_type.take() {
            return Ok(Some(AudioEncodedData {
                payload: Payload::Format(media_type),
            }));
        }
        Ok(self.receiver.recv().await.map(|sample| AudioEncodedData {
            payload: Payload::Sample(sample),
        }))
    }
}

#[derive(Clone, Encode, Decode, Debug)]
pub struct AudioEncodedData {
    pub payload: Payload,
}

impl EncodedData for AudioEncodedData {
    fn timestamp(&self) -> f64 {
        match &self.payload {
            Payload::Sample(sample) => {
                (unsafe { sample.GetSampleTime().unwrap() } as f64) / 10_000_000_f64
            }
            Payload::Format(_media_type) => 0f64,
        }
    }

    fn set_timestamp(&mut self, timestamp: f64) {
        match &self.payload {
            Payload::Sample(sample) => unsafe {
                sample
                    .SetSampleTime((timestamp * 10_000_000_f64) as i64)
                    .unwrap()
            },
            Payload::Format(_media_type) => {}
        };
    }

    fn kind(&self) -> UniencSampleKind {
        match &self.payload {
            Payload::Sample(_sample) => UniencSampleKind::Interpolated,
            Payload::Format(_media_type) => UniencSampleKind::Metadata,
        }
    }
}
