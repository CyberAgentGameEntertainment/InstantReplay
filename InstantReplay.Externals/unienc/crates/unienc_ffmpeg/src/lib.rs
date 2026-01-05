use std::path::Path;
use unienc_common::{EncodingSystem, UnsupportedBlitData};

pub mod audio;
pub mod error;
mod ffmpeg;
pub mod mux;
mod utils;
pub mod video;

pub use error::{FFmpegError, Result};

use audio::FFmpegAudioEncoder;
use mux::FFmpegMuxer;
use video::FFmpegVideoEncoder;

pub struct FFmpegEncodingSystem<
    V: unienc_common::VideoEncoderOptions,
    A: unienc_common::AudioEncoderOptions,
> {
    video_options: V,
    audio_options: A,
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions> EncodingSystem
    for FFmpegEncodingSystem<V, A>
{
    type VideoEncoderOptionsType = V;
    type AudioEncoderOptionsType = A;
    type VideoEncoderType = FFmpegVideoEncoder;
    type AudioEncoderType = FFmpegAudioEncoder;
    type MuxerType = FFmpegMuxer;
    type BlitSourceType = UnsupportedBlitData;

    fn new(video_options: &V, audio_options: &A) -> Self {
        Self {
            video_options: *video_options,
            audio_options: *audio_options,
        }
    }

    fn new_video_encoder(&self) -> unienc_common::Result<Self::VideoEncoderType> {
        FFmpegVideoEncoder::new(&self.video_options).map_err(|e| e.into())
    }

    fn new_audio_encoder(&self) -> unienc_common::Result<Self::AudioEncoderType> {
        FFmpegAudioEncoder::new(&self.audio_options).map_err(|e| e.into())
    }

    fn new_muxer(&self, output_path: &Path) -> unienc_common::Result<Self::MuxerType> {
        FFmpegMuxer::new(output_path, &self.video_options, &self.audio_options).map_err(|e| e.into())
    }
}
