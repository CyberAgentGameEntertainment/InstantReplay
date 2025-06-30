use std::path::Path;

use anyhow::Result;
use serde::{Deserialize, Serialize};

pub trait Encoder {
    type InputType: EncoderInput + 'static;
    type OutputType: EncoderOutput + 'static;
    fn get(self) -> Result<(Self::InputType, Self::OutputType)>;
}

pub trait MuxerCompletionHandle {
    fn finish(self) -> impl Future<Output = Result<()>>;
}

pub trait Muxer: Send {
    type VideoInputType: MuxerInput + 'static;
    type AudioInputType: MuxerInput + 'static;

    fn get_inputs(
        self,
    ) -> Result<(
        Self::VideoInputType,
        Self::AudioInputType,
        impl MuxerCompletionHandle,
    )>;
}

pub trait MuxerInput: Send + 'static {
    type Data: Send;
    fn push(&mut self, data: &Self::Data) -> impl Future<Output = Result<()>> + Send;
    fn finish(self) -> impl Future<Output = Result<()>> + Send;
}

pub trait EncodingSystem {
    type VideoEncoderType: Encoder<InputType: EncoderInput<Data = VideoSample>>;
    type AudioEncoderType: Encoder<InputType: EncoderInput<Data = AudioSample>>;
    type MuxerType: Muxer<
            VideoInputType: MuxerInput<
                Data = <<Self::VideoEncoderType as Encoder>::OutputType as EncoderOutput>::Data,
            >,
            AudioInputType: MuxerInput<
                Data = <<Self::AudioEncoderType as Encoder>::OutputType as EncoderOutput>::Data,
            >,
        >;

    fn new(video_options: &VideoEncoderOptions, audio_options: &AudioEncoderOptions) -> Self;
    fn new_video_encoder(&self) -> Result<Self::VideoEncoderType>;
    fn new_audio_encoder(&self) -> Result<Self::AudioEncoderType>;
    fn new_muxer(&self, output_path: impl AsRef<Path>) -> Result<Self::MuxerType>;
}

#[derive(Copy, Clone)]
pub struct VideoEncoderOptions {
    pub width: u32,
    pub height: u32,
    pub fps_hint: u32,
    pub bitrate: u32,
}

#[derive(Copy, Clone)]
pub struct AudioEncoderOptions {
    pub sample_rate: u32,
    pub channels: u32,
    pub bitrate: u32,
}

pub struct VideoSample {
    pub data: Vec<u8>,
    pub width: u32,
    pub height: u32,
    pub timestamp: f64,
}

pub struct AudioSample {
    pub data: Vec<i16>,
    pub timestamp_in_samples: u64,
}

pub trait EncodedData {
    fn timestamp(&self) -> f64;
}

pub trait EncoderInput: Send + 'static {
    type Data: Send;
    fn push(&mut self, data: &Self::Data) -> impl Future<Output = Result<()>> + Send;
}

pub trait EncoderOutput: Send {
    type Data: EncodedData + Serialize + for<'de> Deserialize<'de> + Send;
    fn pull(&mut self) -> impl Future<Output = Result<Option<Self::Data>>> + Send;
}
