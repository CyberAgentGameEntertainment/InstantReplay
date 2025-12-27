mod audio;
mod emscripten;
mod mux;
mod video;
mod js;

use crate::audio::WebCodecsAudioEncoder;
use crate::mux::WebCodecsMuxer;
use crate::video::WebCodecsVideoEncoder;
use std::path::Path;
use unienc_common::{EncodingSystem, UnsupportedBlitData};

pub struct WebCodecsEncodingSystem<
    V: unienc_common::VideoEncoderOptions,
    A: unienc_common::AudioEncoderOptions,
> {
    video_options: V,
    audio_options: A,
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions> EncodingSystem
    for WebCodecsEncodingSystem<V, A>
{
    type VideoEncoderOptionsType = V;
    type AudioEncoderOptionsType = A;
    type VideoEncoderType = WebCodecsVideoEncoder;
    type AudioEncoderType = WebCodecsAudioEncoder;
    type MuxerType = WebCodecsMuxer;
    type BlitSourceType = UnsupportedBlitData;

    fn new(video_options: &V, audio_options: &A) -> Self {
        Self {
            video_options: *video_options,
            audio_options: *audio_options,
        }
    }

    fn new_video_encoder(&self) -> unienc_common::Result<Self::VideoEncoderType> {
        WebCodecsVideoEncoder::new(&self.video_options).map_err(|e| e.into())
    }

    fn new_audio_encoder(&self) -> unienc_common::Result<Self::AudioEncoderType> {
        WebCodecsAudioEncoder::new(&self.audio_options).map_err(|e| e.into())
    }

    fn new_muxer(&self, output_path: &Path) -> unienc_common::Result<Self::MuxerType> {
        WebCodecsMuxer::new(output_path, &self.video_options, &self.audio_options)
            .map_err(|e| e.into())
    }
}
