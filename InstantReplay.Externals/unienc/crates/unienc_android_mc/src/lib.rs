use anyhow::Result;
use jni::sys::JNI_VERSION_1_6;
use jni::JavaVM;
use std::ffi::{c_int, c_void};
use std::path::Path;
use std::sync::OnceLock;
use unienc_common::{EncodingSystem, TryFromUnityNativeTexturePointer};

pub mod audio;
pub mod common;
pub mod config;
mod java;
pub mod mux;
pub mod video;
mod vulkan;

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
> {
    video_options: V,
    audio_options: A,
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions> EncodingSystem
    for MediaCodecEncodingSystem<V, A>
{
    type VideoEncoderOptionsType = V;
    type AudioEncoderOptionsType = A;
    type VideoEncoderType = MediaCodecVideoEncoder;
    type AudioEncoderType = MediaCodecAudioEncoder;
    type MuxerType = MediaMuxer;
    type BlitSourceType = VulkanTexture;

    fn new(video_options: &V, audio_options: &A) -> Self {
        Self {
            video_options: *video_options,
            audio_options: *audio_options,
        }
    }

    fn new_video_encoder(&self) -> Result<Self::VideoEncoderType> {
        MediaCodecVideoEncoder::new(&self.video_options)
    }

    fn new_audio_encoder(&self) -> Result<Self::AudioEncoderType> {
        MediaCodecAudioEncoder::new(&self.audio_options)
    }

    fn new_muxer(&self, output_path: &Path) -> Result<Self::MuxerType> {
        MediaMuxer::new(output_path, &self.video_options, &self.audio_options)
    }

    fn is_blit_supported(&self) -> bool {
        let s = vulkan::is_initialized();
        s
    }
    fn unity_plugin_load(interfaces: &unity_native_plugin::interface::UnityInterfaces) {
        vulkan::unity_plugin_load(interfaces);
    }
    fn unity_plugin_unload() {}
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
