
#[cfg(not(any(target_vendor = "apple")))]
compile_error!("This crate can only be compiled for Apple platforms.");

use std::{ffi::c_void, path::Path};

use objc2::{rc::Retained, runtime::ProtocolObject};
use objc2_metal::MTLTexture;
use unienc_common::{EncodingSystem, TryFromUnityNativeTexturePointer};

use crate::{
    audio::AudioToolboxEncoder, common::UnsafeSendRetained, mux::AVFMuxer,
    video::VideoToolboxEncoder,
};
mod allocator;
pub mod audio;
mod common;
pub mod error;
mod metal;
pub mod mux;
pub mod video;

pub use error::{AppleError, OsStatusExt, Result};

pub struct VideoToolboxEncodingSystem<
    V: unienc_common::VideoEncoderOptions,
    A: unienc_common::AudioEncoderOptions,
    R: unienc_common::Runtime + 'static,
> {
    video_options: V,
    audio_options: A,
    runtime: R,
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions, R: unienc_common::Runtime + 'static> EncodingSystem
    for VideoToolboxEncodingSystem<V, A, R>
{
    type VideoEncoderOptionsType = V;
    type AudioEncoderOptionsType = A;

    type VideoEncoderType = VideoToolboxEncoder;

    type AudioEncoderType = AudioToolboxEncoder;

    type MuxerType = mux::AVFMuxer;

    type BlitSourceType = MetalTexture;
    type RuntimeType = R;

    fn new(video_options: &V, audio_options: &A, runtime: R) -> Self {
        Self {
            video_options: *video_options,
            audio_options: *audio_options,
            runtime,
        }
    }

    fn new_video_encoder(&self) -> unienc_common::Result<Self::VideoEncoderType> {
        VideoToolboxEncoder::new(&self.video_options).map_err(|e| e.into())
    }

    fn new_audio_encoder(&self) -> unienc_common::Result<Self::AudioEncoderType> {
        AudioToolboxEncoder::new(&self.audio_options).map_err(|e| e.into())
    }

    fn new_muxer(&self, output_path: &Path) -> unienc_common::Result<Self::MuxerType> {
        AVFMuxer::new(output_path, &self.video_options, &self.audio_options).map_err(|e| e.into())
    }

    fn is_blit_supported(&self) -> bool {
        metal::is_initialized()
    }
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions, R: unienc_common::Runtime + 'static> unienc_common::unity::UnityPlugin for VideoToolboxEncodingSystem<V, A, R> {
    fn unity_plugin_load(interfaces: &unity_native_plugin::interface::UnityInterfaces) {
        metal::unity_plugin_load(interfaces);
    }
    fn unity_plugin_unload() {}
}

pub struct MetalTexture {
    pub texture: UnsafeSendRetained<ProtocolObject<dyn MTLTexture>>,
}

impl TryFromUnityNativeTexturePointer for MetalTexture {
    fn try_from_unity_native_texture_ptr(ptr: *mut c_void) -> unienc_common::Result<Self> {
        metal::is_initialized()
            .then_some(())
            .ok_or(AppleError::MetalNotInitialized)?;
        let retained = unsafe { Retained::<ProtocolObject<dyn MTLTexture>>::retain(ptr as *mut _) }
            .ok_or(AppleError::MetalTextureRetainFailed)?;
        Ok(MetalTexture {
            texture: UnsafeSendRetained { inner: retained },
        })
    }
}
