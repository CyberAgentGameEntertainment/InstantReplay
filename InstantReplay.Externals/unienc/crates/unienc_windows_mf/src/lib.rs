use anyhow::Result;
use std::{future::Future, path::Path};
use unienc_common::{EncodingSystem, UnsupportedBlitData};

pub mod audio;
mod common;
pub(crate) mod mft;
pub mod mux;
pub mod video;

use audio::MediaFoundationAudioEncoder;
use mux::MediaFoundationMuxer;
use video::MediaFoundationVideoEncoder;

pub struct MediaFoundationEncodingSystem<
    V: unienc_common::VideoEncoderOptions,
    A: unienc_common::AudioEncoderOptions,
> {
    video_options: V,
    audio_options: A,
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions> EncodingSystem
    for MediaFoundationEncodingSystem<V, A>
{
    type VideoEncoderOptionsType = V;
    type AudioEncoderOptionsType = A;
    type VideoEncoderType = MediaFoundationVideoEncoder;
    type AudioEncoderType = MediaFoundationAudioEncoder;
    type MuxerType = MediaFoundationMuxer;
    type BlitSourceType = UnsupportedBlitData;
    type BlitTargetType = UnsupportedBlitData;

    fn new(video_options: &V, audio_options: &A) -> Self {
        // Initialize Media Foundation
        unsafe {
            let _ = windows::Win32::Media::MediaFoundation::MFStartup(
                windows::Win32::Media::MediaFoundation::MF_VERSION,
                windows::Win32::Media::MediaFoundation::MFSTARTUP_NOSOCKET,
            );
        }

        Self {
            video_options: *video_options,
            audio_options: *audio_options,
        }
    }

    fn new_video_encoder(&self) -> Result<Self::VideoEncoderType> {
        MediaFoundationVideoEncoder::new(&self.video_options)
    }

    fn new_audio_encoder(&self) -> Result<Self::AudioEncoderType> {
        MediaFoundationAudioEncoder::new(&self.audio_options)
    }

    fn new_muxer(&self, output_path: &Path) -> Result<Self::MuxerType> {
        MediaFoundationMuxer::new(output_path, &self.video_options, &self.audio_options)
    }

    fn new_blit_closure(&self, source: Self::BlitSourceType, dst_width: u32, dst_height: u32) -> Result<Box<dyn FnOnce() -> std::pin::Pin<Box<dyn Future<Output = Result<Self::BlitTargetType>> + Send>> + Send>> {
        Err(anyhow::anyhow!("Media Foundation Encoding System does not support blitting"))
    }
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions> Drop
    for MediaFoundationEncodingSystem<V, A>
{
    fn drop(&mut self) {
        unsafe {
            let _ = windows::Win32::Media::MediaFoundation::MFShutdown();
        }
    }
}
