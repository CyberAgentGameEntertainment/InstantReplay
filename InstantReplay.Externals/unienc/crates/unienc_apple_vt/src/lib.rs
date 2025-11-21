use std::{ffi::c_void, future::Future, path::Path, pin::Pin};

use objc2::{rc::Retained, runtime::ProtocolObject};
use objc2_metal::MTLTexture;
use unienc_common::{BlitOptions, EncodingSystem, TryFromUnityNativeTexturePointer};

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
    type BlitTargetType = SharedTexture;

    fn new_video_encoder(&self) -> Result<Self::VideoEncoderType> {
        VideoToolboxEncoder::new(&self.video_options)
    }

    fn new_audio_encoder(&self) -> Result<Self::AudioEncoderType> {
        AudioToolboxEncoder::new(&self.audio_options)
    }

    fn new_muxer(&self, output_path: &Path) -> Result<Self::MuxerType> {
        AVFMuxer::new(output_path, &self.video_options, &self.audio_options)
    }

    fn new(video_options: &V, audio_options: &A) -> Self {
        Self {
            video_options: *video_options,
            audio_options: *audio_options,
        }
    }

    fn is_blit_supported(&self) -> bool {
        true
    }

    fn new_blit_closure(
        &self,
        source: Self::BlitSourceType,
        options: BlitOptions,
    ) -> Result<
        Box<dyn FnOnce() -> Pin<Box<dyn Future<Output = Result<Self::BlitTargetType>> + Send>> + Send>,
    > {
        let tex = UnsafeSendRetained::from((*source.texture).clone());
        Ok(Box::new(move || {
            let tex = tex.clone();
            let res = metal::custom_blit(&tex, options);
            Box::pin(async move {
                match res {
                    Ok(future) => future.await,
                    Err(err) => Err(anyhow::anyhow!("Failed to perform blit operation: {}", err)),
                }
            })
        }))
    }
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
