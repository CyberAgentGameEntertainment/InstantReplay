
#[cfg(not(any(target_os = "windows")))]
compile_error!("This crate can only be compiled for Windows platforms.");

use std::path::Path;
use unienc_common::{EncodingSystem, Runtime, UnsupportedBlitData};

pub mod audio;
mod common;
pub mod error;
pub(crate) mod mft;
pub mod mux;
pub mod video;

pub use error::{WindowsError, Result};

use audio::MediaFoundationAudioEncoder;
use mux::MediaFoundationMuxer;
use video::MediaFoundationVideoEncoder;

pub struct MediaFoundationEncodingSystem<
    V: unienc_common::VideoEncoderOptions,
    A: unienc_common::AudioEncoderOptions,
    R: Runtime,
> {
    video_options: V,
    audio_options: A,
    runtime: R,
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions, R: Runtime + 'static> EncodingSystem
    for MediaFoundationEncodingSystem<V, A, R>
{
    type VideoEncoderOptionsType = V;
    type AudioEncoderOptionsType = A;
    type VideoEncoderType = MediaFoundationVideoEncoder;
    type AudioEncoderType = MediaFoundationAudioEncoder;
    type MuxerType = MediaFoundationMuxer;
    type BlitSourceType = UnsupportedBlitData;
    type RuntimeType = R;

    fn new(video_options: &V, audio_options: &A, runtime: R) -> Self {
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
            runtime,
        }
    }

    fn new_video_encoder(&self) -> unienc_common::Result<Self::VideoEncoderType> {
        MediaFoundationVideoEncoder::new(&self.video_options, &self.runtime).map_err(|e| e.into())
    }

    fn new_audio_encoder(&self) -> unienc_common::Result<Self::AudioEncoderType> {
        MediaFoundationAudioEncoder::new(&self.audio_options, &self.runtime).map_err(|e| e.into())
    }

    fn new_muxer(&self, output_path: &Path) -> unienc_common::Result<Self::MuxerType> {
        MediaFoundationMuxer::new(output_path, &self.video_options, &self.audio_options, &self.runtime).map_err(|e| e.into())
    }
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions, R: Runtime> Drop
    for MediaFoundationEncodingSystem<V, A, R>
{
    fn drop(&mut self) {
        unsafe {
            let _ = windows::Win32::Media::MediaFoundation::MFShutdown();
        }
    }
}
