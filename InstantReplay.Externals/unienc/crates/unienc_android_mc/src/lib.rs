use anyhow::Result;
use jni::sys::JNI_VERSION_1_6;
use jni::JavaVM;
use std::ffi::{c_int, c_void};
use std::path::Path;
use std::sync::{Arc, OnceLock};
use unienc_common::{EncodingSystem, TryFromUnityNativeTexturePointer};

pub mod audio;
pub mod common;
pub mod config;
mod java;
pub mod mux;
pub mod video;
mod vulkan;

use crate::vulkan::types::VulkanSemaphoreHandle;
use audio::MediaCodecAudioEncoder;
use mux::MediaMuxer;
use video::MediaCodecVideoEncoder;

static JAVA_VM: OnceLock<jni::JavaVM> = OnceLock::new();

pub unsafe fn set_java_vm(vm: *mut jni::sys::JavaVM, _reserved: *mut c_void) -> c_int {
    JAVA_VM.set(JavaVM::from_raw(vm).unwrap()).unwrap();
    println!("JNI_OnLoad: {:?}", vm);
    JNI_VERSION_1_6
}

pub struct MediaCodecEncodingSystem<
    V: unienc_common::VideoEncoderOptions,
    A: unienc_common::AudioEncoderOptions,
    G: unienc_common::GraphicsEventIssuer,
> {
    video_options: V,
    audio_options: A,
    _event_issuer_marker: std::marker::PhantomData<G>,
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions, G: unienc_common::GraphicsEventIssuer> EncodingSystem
    for MediaCodecEncodingSystem<V, A, G>
{
    type VideoEncoderOptionsType = V;
    type AudioEncoderOptionsType = A;
    type VideoEncoderType = MediaCodecVideoEncoder<G>;
    type AudioEncoderType = MediaCodecAudioEncoder;
    type MuxerType = MediaMuxer;
    type BlitSourceType = VulkanTexture;
    type GraphicsEventIssuerType = G;

    fn new(video_options: &V, audio_options: &A) -> Self {
        Self {
            video_options: *video_options,
            audio_options: *audio_options,
            _event_issuer_marker: std::marker::PhantomData,
        }
    }

    fn new_video_encoder(&self, event_issuer: G) -> Result<Self::VideoEncoderType> {
        MediaCodecVideoEncoder::new(&self.video_options, event_issuer)
    }

    fn new_audio_encoder(&self) -> Result<Self::AudioEncoderType> {
        MediaCodecAudioEncoder::new(&self.audio_options)
    }

    fn new_muxer(&self, output_path: &Path) -> Result<Self::MuxerType> {
        MediaMuxer::new(output_path, &self.video_options, &self.audio_options)
    }

    fn is_blit_supported(&self) -> bool {
        let s = vulkan::is_initialized();

        println!("is_blit_supported: {s}");
        s
    }
}

pub struct VulkanTexture {
    tex: ash::vk::Image,
}

impl TryFromUnityNativeTexturePointer for VulkanTexture {
    fn try_from_unity_native_texture_ptr(ptr: *mut c_void) -> Result<Self> {
        // ptr is VkImage*
        let ptr = ptr as *mut ash::vk::Image;
        if ptr.is_null() {
            return Err(anyhow::anyhow!("Null Vulkan texture pointer"));
        }
        Ok(VulkanTexture {
            tex: unsafe { *ptr },
        })
    }
}
