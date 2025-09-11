use anyhow::Result;
use bincode::{Decode, Encode};
use tokio::sync::mpsc;
use unienc_common::{
    EncodedData, Encoder, EncoderInput, EncoderOutput, UniencDataKind, VideoEncoderOptions,
    VideoSample,
};
use windows::Win32::Media::MediaFoundation::*;

use crate::common::*;
use crate::mft::Transform;

pub struct MediaFoundationVideoEncoder {
    transform: Transform,
    output_rx: mpsc::Receiver<UnsafeSend<IMFSample>>,
    fps_hint: f64,
}

impl MediaFoundationVideoEncoder {
    pub fn new<V: VideoEncoderOptions>(options: &V) -> Result<Self> {
        let input_type = unsafe {
            let input_type = MFCreateMediaType()?;
            input_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
            input_type.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_NV12)?;
            input_type.SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)?;

            input_type.SetUINT64(
                &MF_MT_FRAME_SIZE,
                ((options.width() as u64) << 32) + options.height() as u64,
            )?;

            input_type.SetUINT64(&MF_MT_FRAME_RATE, ((options.fps_hint() as u64) << 32) + 1)?;
            input_type
        };

        let output_type = unsafe {
            let output_type = MFCreateMediaType()?;
            output_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
            output_type.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_H264)?;
            output_type.SetUINT32(&MF_MT_AVG_BITRATE, options.bitrate())?;
            output_type.SetUINT64(&MF_MT_FRAME_RATE, ((options.fps_hint() as u64) << 32) + 1)?;
            output_type.SetUINT64(
                &MF_MT_FRAME_SIZE,
                ((options.width() as u64) << 32) + options.height() as u64,
            )?;
            output_type.SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)?;
            output_type.SetUINT32(&MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base.0 as u32)?;
            output_type
        };

        let (transform, output_rx) = Transform::new(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFT_REGISTER_TYPE_INFO {
                guidMajorType: MFMediaType_Video,
                guidSubtype: MFVideoFormat_NV12,
            },
            MFT_REGISTER_TYPE_INFO {
                guidMajorType: MFMediaType_Video,
                guidSubtype: MFVideoFormat_H264,
            },
            input_type,
            output_type,
        )?;

        Ok(Self {
            transform,
            output_rx,
            fps_hint: options.fps_hint() as f64,
        })
    }
}

impl Encoder for MediaFoundationVideoEncoder {
    type InputType = VideoEncoderInputImpl;
    type OutputType = VideoEncoderOutputImpl;

    fn get(self) -> Result<(Self::InputType, Self::OutputType)> {
        let media_type = Some(UnsafeSend(self.transform.output_type()?.clone()));
        Ok((
            VideoEncoderInputImpl {
                transform: self.transform,
                fps_hint: self.fps_hint,
            },
            VideoEncoderOutputImpl {
                receiver: self.output_rx,
                media_type,
            },
        ))
    }
}

pub struct VideoEncoderInputImpl {
    transform: Transform,
    fps_hint: f64,
}

pub struct VideoEncoderOutputImpl {
    media_type: Option<UnsafeSend<IMFMediaType>>,
    receiver: mpsc::Receiver<UnsafeSend<IMFSample>>,
}

impl EncoderInput for VideoEncoderInputImpl {
    type Data = VideoSample;

    async fn push(&mut self, data: &Self::Data) -> Result<()> {
        let sample = UnsafeSend(unsafe { MFCreateSample()? });

        // BGRA to NV12
        {
            let (y, u, v) = data.to_yuv420_planes(None)?;
            let length = (y.len() + u.len() + v.len()) as u32;
            let buffer = unsafe { MFCreateMemoryBuffer(length)? };

            unsafe { sample.AddBuffer(&buffer)? };

            let mut buffer_ptr: *mut u8 = std::ptr::null_mut();
            unsafe { buffer.Lock(&mut buffer_ptr, None, None)? };

            unsafe {
                std::ptr::copy_nonoverlapping(y.as_ptr(), buffer_ptr, y.len());
                buffer_ptr = buffer_ptr.add(y.len());
                for (i, &val) in u.iter().enumerate() {
                    *buffer_ptr.add(i * 2) = val;
                }
                for (i, &val) in v.iter().enumerate() {
                    *buffer_ptr.add(i * 2 + 1) = val;
                }
            }

            unsafe { buffer.SetCurrentLength(length)? }

            unsafe { buffer.Unlock()? };
        }

        unsafe { sample.SetSampleTime((data.timestamp * 10_000_000_f64) as i64)? };
        unsafe { sample.SetSampleDuration((1.0_f64 / self.fps_hint * 10_000_000_f64) as i64)? };
        self.transform.push(sample).await
    }
}

impl EncoderOutput for VideoEncoderOutputImpl {
    type Data = VideoEncodedData;

    async fn pull(&mut self) -> Result<Option<Self::Data>> {
        if let Some(media_type) = self.media_type.take() {
            return Ok(Some(VideoEncodedData {
                payload: Payload::Format(media_type),
            }));
        }
        Ok(self.receiver.recv().await.map(|sample| VideoEncodedData {
            payload: Payload::Sample(sample),
        }))
    }
}

#[derive(Clone, Encode, Decode, Debug)]
pub struct VideoEncodedData {
    pub payload: Payload,
}

impl EncodedData for VideoEncodedData {
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
            Payload::Sample(sample) => {
                let sample_time = (timestamp * 10_000_000_f64) as i64;
                unsafe { sample.SetSampleTime(sample_time) }.unwrap();
                // set the DTS
                // it assumes there is no B frame
                if unsafe { sample.GetItemType(&MFSampleExtension_DecodeTimestamp) }.is_ok() {
                    unsafe {
                        sample
                            .SetUINT64(&MFSampleExtension_DecodeTimestamp, sample_time as u64)
                            .unwrap()
                    };
                }
            }
            Payload::Format(_media_type) => {}
        };
    }

    fn kind(&self) -> UniencDataKind {
        match &self.payload {
            Payload::Sample(sample) => {
                if unsafe { sample.GetUINT32(&MFSampleExtension_CleanPoint).unwrap() } != 0 {
                    UniencDataKind::Key
                } else {
                    UniencDataKind::Interpolated
                }
            }
            Payload::Format(_media_type) => UniencDataKind::Metadata,
        }
    }
}
