use std::path::Path;

use unienc_common::EncodingSystem;

use crate::{audio::AudioToolboxEncoder, mux::AVFMuxer, video::VideoToolboxEncoder};
use anyhow::Result;

pub mod audio;
mod common;
pub mod mux;
pub mod video;

pub struct VideoToolboxEncodingSystem
{
    video_options: unienc_common::VideoEncoderOptions,
    audio_options: unienc_common::AudioEncoderOptions,
}

impl EncodingSystem for VideoToolboxEncodingSystem {
    type VideoEncoderType = VideoToolboxEncoder;

    type AudioEncoderType = AudioToolboxEncoder;

    type MuxerType = mux::AVFMuxer;

    fn new_video_encoder(
        &self,
    ) -> Result<Self::VideoEncoderType> {
        VideoToolboxEncoder::new(&self.video_options)
    }

    fn new_audio_encoder(
        &self,
    ) -> Result<Self::AudioEncoderType> {
        AudioToolboxEncoder::new(&self.audio_options)
    }

    fn new_muxer(&self, output_path: impl AsRef<Path>) -> Result<Self::MuxerType> {
        AVFMuxer::new(output_path, &self.video_options, &self.audio_options)
    }
    
    fn new(video_options: &unienc_common::VideoEncoderOptions, audio_options: &unienc_common::AudioEncoderOptions) -> Self {
        Self {
            video_options: *video_options,
            audio_options: *audio_options,
        }
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