use anyhow::Result;
use std::{future::Future, path::Path};
use unienc_common::{EncodingSystem, UnsupportedBlitData};

pub mod audio;
mod ffmpeg;
pub mod mux;
mod utils;
pub mod video;

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
    type BlitTargetType = UnsupportedBlitData;

    fn new(video_options: &V, audio_options: &A) -> Self {
        Self {
            video_options: *video_options,
            audio_options: *audio_options,
        }
    }

    fn new_video_encoder(&self) -> Result<Self::VideoEncoderType> {
        FFmpegVideoEncoder::new(&self.video_options)
    }

    fn new_audio_encoder(&self) -> Result<Self::AudioEncoderType> {
        FFmpegAudioEncoder::new(&self.audio_options)
    }

    fn new_muxer(&self, output_path: &Path) -> Result<Self::MuxerType> {
        FFmpegMuxer::new(output_path, &self.video_options, &self.audio_options)
    }

    fn new_blit_closure(
        &self,
        source: Self::BlitSourceType,
        dst_width: u32,
        dst_height: u32,
    ) -> Result<
        Box<
            dyn FnOnce() -> std::pin::Pin<
                    Box<dyn Future<Output = Result<Self::BlitTargetType>> + Send>,
                > + Send,
        >,
    > {
        Err(anyhow::anyhow!(
            "Blit not supported in FFmpeg encoding system"
        ))
    }
}
