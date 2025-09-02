use std::path::Path;

use anyhow::{Context, Result};
use tokio::io::AsyncWriteExt;
use unienc_common::{CompletionHandle, Muxer, MuxerInput};

use crate::{
    audio::AudioEncodedData,
    ffmpeg::{self, FFmpeg},
    video::VideoEncodedData,
};

pub struct FFmpegMuxer {
    video: FFmpegMuxerVideoInput,
    audio: FFmpegMuxerAudioInput,
    completion: FFmpegCompletionHandle,
}

pub struct FFmpegCompletionHandle {
    child: FFmpeg,
}

pub struct FFmpegMuxerVideoInput {
    input: ffmpeg::Input,
}

pub struct FFmpegMuxerAudioInput {
    input: ffmpeg::Input,
}

impl FFmpegMuxer {
    pub fn new<P: AsRef<Path>>(
        output_path: P,
        video_options: &impl unienc_common::VideoEncoderOptions,
        audio_options: &impl unienc_common::AudioEncoderOptions,
    ) -> Result<Self> {
        let mut ffmpeg = ffmpeg::Builder::new()
            .input([
                "-f",
                "h264",
                "-framerate",
                &format!("{}", video_options.fps_hint()),
            ])
            .input(["-f", "aac", "-ac", &format!("{}", audio_options.channels())])
            .build(
                [
                    "-pix_fmt",
                    "yuv420p",
                    "-c:v",
                    "copy",
                    "-c:a",
                    "copy",
                    "-ar",
                    &format!("{}", audio_options.sample_rate()),
                    "-r",
                    &format!("{}", video_options.fps_hint()),
                    "-f",
                    "mp4",
                ],
                ffmpeg::Destination::Path(output_path.as_ref().as_os_str().to_owned()),
            )?;

        let mut inputs = ffmpeg.inputs.take().context("failed to get inputs")?;
        let audio_input = inputs.remove(1);
        let video_input = inputs.remove(0);

        Ok(FFmpegMuxer {
            video: FFmpegMuxerVideoInput { input: video_input },
            audio: FFmpegMuxerAudioInput { input: audio_input },
            completion: FFmpegCompletionHandle { child: ffmpeg },
        })
    }
}

impl Muxer for FFmpegMuxer {
    type VideoInputType = FFmpegMuxerVideoInput;
    type AudioInputType = FFmpegMuxerAudioInput;
    type CompletionHandleType = FFmpegCompletionHandle;

    fn get_inputs(
        self,
    ) -> anyhow::Result<(
        Self::VideoInputType,
        Self::AudioInputType,
        Self::CompletionHandleType,
    )> {
        Ok((self.video, self.audio, self.completion))
    }
}

impl MuxerInput for FFmpegMuxerVideoInput {
    type Data = VideoEncodedData;

    async fn push(&mut self, data: Self::Data) -> anyhow::Result<()> {
        match data {
            VideoEncodedData::ParameterSet(payload) => {
                self.input.write_all(&payload).await?;
            }
            VideoEncodedData::Slice { payload, .. } => {
                self.input.write_all(&payload).await?;
            }
        }
        Ok(())
    }

    async fn finish(mut self) -> Result<()> {
        self.input.shutdown().await?;
        Ok(())
    }
}

impl MuxerInput for FFmpegMuxerAudioInput {
    type Data = AudioEncodedData;

    async fn push(&mut self, data: Self::Data) -> Result<()> {
        self.input.write_all(&data.header).await?;
        self.input.write_all(&data.payload).await?;
        Ok(())
    }

    async fn finish(mut self) -> Result<()> {
        self.input.shutdown().await?;
        Ok(())
    }
}

impl CompletionHandle for FFmpegCompletionHandle {
    async fn finish(self) -> Result<()> {
        if self.child.wait().await?.success() {
            Ok(())
        } else {
            Err(anyhow::anyhow!("ffmpeg process failed"))
        }
    }
}
