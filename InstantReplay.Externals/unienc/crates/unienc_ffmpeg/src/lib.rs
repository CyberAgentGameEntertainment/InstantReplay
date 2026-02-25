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
    R: unienc_common::Runtime,
> {
    video_options: V,
    audio_options: A,
    _runtime: std::marker::PhantomData<R>,
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions, R: unienc_common::Runtime> EncodingSystem
    for FFmpegEncodingSystem<V, A, R>
{
    type VideoEncoderOptionsType = V;
    type AudioEncoderOptionsType = A;
    type VideoEncoderType = FFmpegVideoEncoder;
    type AudioEncoderType = FFmpegAudioEncoder;
    type MuxerType = FFmpegMuxer;
    type BlitSourceType = UnsupportedBlitData;
    type RuntimeType = R;

    fn new(video_options: &V, audio_options: &A, runtime: R) -> Self {
        Self {
            video_options: *video_options,
            audio_options: *audio_options,
            _runtime: std::marker::PhantomData,
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
