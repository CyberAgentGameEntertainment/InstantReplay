use std::path::Path;
use std::future::Future;

use anyhow::Result;
use bincode::{Decode, Encode};

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

    fn new(video_options: &Self::VideoEncoderOptionsType, audio_options: &Self::AudioEncoderOptionsType) -> Self;
    fn new_video_encoder(&self) -> Result<Self::VideoEncoderType>;
    fn new_audio_encoder(&self) -> Result<Self::AudioEncoderType>;
    fn new_muxer(&self, output_path: &Path) -> Result<Self::MuxerType>;
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

#[derive(Clone)]
pub struct VideoSample {
    pub data: Vec<u8>, // ARGB32 input data
    pub width: u32,
    pub height: u32,
    pub timestamp: f64,
}

#[derive(Clone)]
pub struct AudioSample {
    pub data: Vec<i16>,
    pub timestamp_in_samples: u64,
}

pub trait EncodedData: Encode + Decode<()> {
    fn timestamp(&self) -> f64;
    fn set_timestamp(&mut self, timestamp: f64);
    fn is_key(&self) -> bool;
}

pub trait EncoderInput: Send + 'static {
    type Data: Send;
    fn push(&mut self, data: &Self::Data) -> impl Future<Output = Result<()>> + Send;
}

pub trait EncoderOutput: Send {
    type Data: EncodedData + Send;
    fn pull(&mut self) -> impl Future<Output = Result<Option<Self::Data>>> + Send;
}
