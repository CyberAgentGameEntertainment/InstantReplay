use std::ffi::{c_int, c_void};
use std::future::Future;
use std::path::Path;
use std::sync::OnceLock;
use jni::JavaVM;
use unienc_common::{EncodingSystem, UnsupportedBlitData};
use anyhow::Result;

pub mod audio;
pub mod common;
pub mod config;
pub mod mux;
pub mod video;
mod java;

use audio::MediaCodecAudioEncoder;
use mux::MediaMuxer;
use video::MediaCodecVideoEncoder;

static JAVA_VM: OnceLock<jni::JavaVM> = OnceLock::new();

pub unsafe fn set_java_vm(vm: *mut jni::sys::JavaVM, _reserved: *mut c_void) -> c_int {
    JAVA_VM.set(JavaVM::from_raw(vm).unwrap()).unwrap();
    println!("JNI_OnLoad: {:?}", vm);
    0
}

pub struct MediaCodecEncodingSystem<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions> {
    video_options: V,
    audio_options: A,
}

impl<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions> EncodingSystem for MediaCodecEncodingSystem<V, A> {
    type VideoEncoderOptionsType = V;
    type AudioEncoderOptionsType = A;
    type VideoEncoderType = MediaCodecVideoEncoder;
    type AudioEncoderType = MediaCodecAudioEncoder;
    type MuxerType = MediaMuxer;
    type BlitSourceType = UnsupportedBlitData;
    type BlitTargetType = UnsupportedBlitData;

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
            "Blit not supported in MediaCodec encoding system"
        ))
    }
}
