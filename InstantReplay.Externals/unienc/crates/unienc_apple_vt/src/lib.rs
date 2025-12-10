
#[cfg(not(any(target_vendor = "apple")))]
compile_error!("This crate can only be compiled for Apple platforms.");

use std::{ffi::c_void, future::Future, path::Path, pin::Pin};

use objc2::{rc::Retained, runtime::ProtocolObject};
use objc2_metal::MTLTexture;
use unienc_common::{EncodingSystem, TryFromUnityNativeTexturePointer};

use crate::{
    audio::AudioToolboxEncoder, common::UnsafeSendRetained, metal::SharedTexture, mux::AVFMuxer,
    video::VideoToolboxEncoder,
};
use anyhow::Result;

pub mod audio;
mod common;
mod metal;
pub mod mux;
pub mod video;

pub struct VideoToolboxEncodingSystem<
    V: unienc_common::VideoEncoderOptions,
    A: unienc_common::AudioEncoderOptions,
> {
    video_options: V,
    audio_options: A,
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions> EncodingSystem
    for VideoToolboxEncodingSystem<V, A>
{
    type VideoEncoderOptionsType = V;
    type AudioEncoderOptionsType = A;

    type VideoEncoderType = VideoToolboxEncoder;

    type AudioEncoderType = AudioToolboxEncoder;

    type MuxerType = mux::AVFMuxer;

    type BlitSourceType = MetalTexture;

    fn new(video_options: &V, audio_options: &A) -> Self {
        Self {
            video_options: *video_options,
            audio_options: *audio_options,
        }
    }

    fn new_video_encoder(&self) -> Result<Self::VideoEncoderType> {
        VideoToolboxEncoder::new(&self.video_options)
    }

    fn new_audio_encoder(&self) -> Result<Self::AudioEncoderType> {
        AudioToolboxEncoder::new(&self.audio_options)
    }

    fn new_muxer(&self, output_path: &Path) -> Result<Self::MuxerType> {
        AVFMuxer::new(output_path, &self.video_options, &self.audio_options)
    }

    fn is_blit_supported(&self) -> bool {
        metal::is_initialized()
    }

    fn unity_plugin_load(interfaces: &unity_native_plugin::interface::UnityInterfaces) {
        metal::unity_plugin_load(interfaces);
    }
    fn unity_plugin_unload() {}
}

pub struct MetalTexture {
    pub texture: UnsafeSendRetained<ProtocolObject<dyn MTLTexture>>,
}

impl TryFromUnityNativeTexturePointer for MetalTexture {
    fn try_from_unity_native_texture_ptr(ptr: *mut c_void) -> Result<Self> {
        metal::is_initialized()
            .then_some(())
            .ok_or_else(|| anyhow::anyhow!("Metal context is not initialized"))?;
        let retained = unsafe { Retained::<ProtocolObject<dyn MTLTexture>>::retain(ptr as *mut _) }
            .ok_or_else(|| anyhow::anyhow!("Failed to retain MTLTexture from raw pointer"))?;
        Ok(MetalTexture {
            texture: UnsafeSendRetained { inner: retained },
        })
    }
}

pub(crate) trait OsStatus {
    fn to_result(&self) -> Result<()>;
}

impl OsStatus for i32 {
    fn to_result(&self) -> Result<()> {
        if *self == 0 {
            Ok(())
        } else {
            Err(anyhow::anyhow!("OSStatus: {}", *self))
        }
    }
}
