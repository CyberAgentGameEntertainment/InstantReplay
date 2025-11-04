use std::ffi::c_void;
use std::fmt::Debug;
use std::pin::Pin;
use std::{future::Future};
use std::path::Path;

use anyhow::Result;
use bincode::{Decode, Encode};

use crate::buffer::SharedBuffer;

pub mod buffer;

pub trait Encoder {
    type InputType: EncoderInput + 'static;
    type OutputType: EncoderOutput + 'static;
    fn get(self) -> Result<(Self::InputType, Self::OutputType)>;
}

pub trait CompletionHandle {
    fn finish(self) -> impl Future<Output = Result<()>> + Send;
}

pub trait Muxer: Send {
    type VideoInputType: MuxerInput + 'static;
    type AudioInputType: MuxerInput + 'static;
    type CompletionHandleType: CompletionHandle + 'static;

    fn get_inputs(
        self,
    ) -> Result<(
        Self::VideoInputType,
        Self::AudioInputType,
        Self::CompletionHandleType,
    )>;
}

pub trait MuxerInput: Send + 'static {
    type Data: Send;
    fn push(&mut self, data: Self::Data) -> impl Future<Output = Result<()>> + Send;
    fn finish(self) -> impl Future<Output = Result<()>> + Send;
}

pub trait EncodingSystem {
    type VideoEncoderOptionsType: VideoEncoderOptions;
    type AudioEncoderOptionsType: AudioEncoderOptions;
    type VideoEncoderType: Encoder<InputType: EncoderInput<Data = VideoSample<Self::BlitTargetType>>>;
    type AudioEncoderType: Encoder<InputType: EncoderInput<Data = AudioSample>>;
    type MuxerType: Muxer<
        VideoInputType: MuxerInput<
            Data = <<Self::VideoEncoderType as Encoder>::OutputType as EncoderOutput>::Data,
        >,
        AudioInputType: MuxerInput<
            Data = <<Self::AudioEncoderType as Encoder>::OutputType as EncoderOutput>::Data,
        >,
    >;
    type BlitSourceType: TryFromUnityNativeTexturePointer + Send;
    type BlitTargetType: TryFromRaw + IntoRaw + Send;

    fn new(
        video_options: &Self::VideoEncoderOptionsType,
        audio_options: &Self::AudioEncoderOptionsType,
    ) -> Self;
    fn new_video_encoder(&self) -> Result<Self::VideoEncoderType>;
    fn new_audio_encoder(&self) -> Result<Self::AudioEncoderType>;
    fn new_muxer(&self, output_path: &Path) -> Result<Self::MuxerType>;
    fn new_blit_closure(&self, source: Self::BlitSourceType, dst_width: u32, dst_height: u32) -> Result<Box<dyn FnOnce() -> Pin<Box<dyn Future<Output = Result<Self::BlitTargetType>> + Send>> + Send>>;
}

pub trait TryFromUnityNativeTexturePointer: Sized {
    fn try_from_unity_native_texture_ptr(ptr: *mut c_void) -> Result<Self>;
}

pub trait TryFromRaw: Sized {
    unsafe fn try_from_raw(ptr: *mut c_void) -> Result<Self>;
}

pub trait IntoRaw {
    fn into_raw(self) -> *mut c_void;
}

pub trait VideoEncoderOptions: Clone + Copy {
    fn width(&self) -> u32;
    fn height(&self) -> u32;
    fn fps_hint(&self) -> u32;
    fn bitrate(&self) -> u32;
}

pub trait AudioEncoderOptions: Clone + Copy {
    fn sample_rate(&self) -> u32;
    fn channels(&self) -> u32;
    fn bitrate(&self) -> u32;
}

// #[derive(Clone)]
pub struct VideoSample<BlitTargetType> {
    pub frame: VideoFrame<BlitTargetType>,
    pub timestamp: f64,
}

pub enum VideoFrame<BlitTargetType> {
    Bgra32(VideoFrameBgra32),
    BlitTarget(BlitTargetType),
}

pub struct VideoFrameBgra32 {
    pub buffer: SharedBuffer,
    pub width: u32,
    pub height: u32,
}

impl VideoFrameBgra32 {
    pub fn to_yuv420_planes(
        &self,
        padded_size: Option<(u32, u32)>,
    ) -> Result<(Vec<u8>, Vec<u8>, Vec<u8>)> {
        let data = self.buffer.data();
        let w = padded_size.map_or(self.width, |(w, _)| w);
        let h = padded_size.map_or(self.height, |(_, h)| h);
        let padded_y_size = (w * h) as usize;
        let padded_uv_size = (w * h / 4) as usize;

        // Create padded YUV data arrays
        let mut y_data = vec![16u8; padded_y_size]; // Black level for Y
        let mut u_data = vec![128u8; padded_uv_size]; // Neutral for U
        let mut v_data = vec![128u8; padded_uv_size]; // Neutral for V

        // Convert ARGB to YUV for the original image area only
        for y in 0..self.height {
            for x in 0..self.width {
                let bgra_idx = ((y * self.width + x) * 4) as usize;
                let r = data[bgra_idx + 2] as i32;
                let g = data[bgra_idx + 1] as i32;
                let b = data[bgra_idx] as i32;

                let y_val = (((66 * r + 129 * g + 25 * b + 128) >> 8) + 16) as u8;

                let y_idx = (y * w + x) as usize;
                y_data[y_idx] = y_val;

                // Sample U and V for every 2x2 block (4:2:0 subsampling)
                if x % 2 == 0 && y % 2 == 0 {
                    let u_val = (((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128) as u8;
                    let v_val = (((112 * r - 94 * g - 18 * b + 128) >> 8) + 128) as u8;

                    let uv_idx = ((y / 2) * (w / 2) + (x / 2)) as usize;
                    u_data[uv_idx] = u_val;
                    v_data[uv_idx] = v_val;
                }
            }
        }

        Ok((y_data, u_data, v_data))
    }
}

#[derive(Clone)]
pub struct AudioSample {
    pub data: Vec<i16>,
    pub timestamp_in_samples: u64,
}

pub trait EncodedData: Encode + Decode<()> {
    fn timestamp(&self) -> f64;
    fn set_timestamp(&mut self, timestamp: f64);
    fn kind(&self) -> UniencSampleKind;
}

#[repr(i8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UniencSampleKind {
    Interpolated = 0,
    Key = 1,
    Metadata = 2,
}

pub trait EncoderInput: Send + 'static {
    type Data: Send;
    fn push(&mut self, data: Self::Data) -> impl Future<Output = Result<()>> + Send;
}

pub trait EncoderOutput: Send {
    type Data: EncodedData + Send;
    fn pull(&mut self) -> impl Future<Output = Result<Option<Self::Data>>> + Send;
}
