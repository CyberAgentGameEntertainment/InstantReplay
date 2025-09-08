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
    input: Option<ffmpeg::Input>,
}

pub struct FFmpegMuxerAudioInput {
    input: Option<ffmpeg::Input>,
}

impl FFmpegMuxer {
    pub fn new<P: AsRef<Path>>(
        output_path: P,
        video_options: &impl unienc_common::VideoEncoderOptions,
        audio_options: &impl unienc_common::AudioEncoderOptions,
    ) -> Result<Self> {
        // raw H.264 frame cannot have timestamp, so we need to assume CFR (encoder also supports CFR)
        let mut ffmpeg = ffmpeg::Builder::new()
            .use_stdin(true)
            .input([
                "-f",
                "h264",
                "-r",
                &format!("{}", video_options.fps_hint()),
            ])
            .input(["-f", "aac"])
            .build(
                [
                    "-pix_fmt",
                    "yuv420p",
                    "-c:v",
                    "copy",
                    "-c:a",
                    "copy",
                    "-f",
                    "mp4",
                ],
                ffmpeg::Destination::Path(output_path.as_ref().as_os_str().to_owned()),
            )?;

        let mut inputs = ffmpeg.inputs.take().context("failed to get inputs")?;
        let audio_input = inputs.remove(1);
        let video_input = inputs.remove(0);

        Ok(FFmpegMuxer {
            video: FFmpegMuxerVideoInput { input: Some(video_input) },
            audio: FFmpegMuxerAudioInput { input: Some(audio_input) },
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
        let input = self.input.as_mut().context(anyhow::anyhow!("Input is None"))?;
        match data {
            VideoEncodedData::ParameterSet(payload) => {
                input.write_all(&payload).await?;
            }
            VideoEncodedData::Slice { payload, .. } => {
                input.write_all(&payload).await?;
            }
        }

        input.flush().await?;

        Ok(())
    }

    async fn finish(mut self) -> Result<()> {
        // take input to drop it to ensure stdin / pipe is closed
        self.input.take().context("Failed to take input")?.shutdown().await?;
        Ok(())
    }
}

impl MuxerInput for FFmpegMuxerAudioInput {
    type Data = AudioEncodedData;

    async fn push(&mut self, data: Self::Data) -> Result<()> {
        let input = self.input.as_mut().context("Input is None")?;
        input.write_all(&data.header).await?;
        input.write_all(&data.payload).await?;

        input.flush().await?;

        Ok(())
    }

    async fn finish(mut self) -> Result<()> {
        // take input to drop it to ensure stdin / pipe is closed
        self.input.take().context("Failed to take input")?.shutdown().await?;
        Ok(())
    }
}

impl CompletionHandle for FFmpegCompletionHandle {
    async fn finish(self) -> Result<()> {
        let result = self.child.wait().await?;
        println!("FFmpeg exited: {}", result);
        if result.success() {
            Ok(())
        } else {
            Err(anyhow::anyhow!("FFmpeg process failed"))
        }
    }
}
