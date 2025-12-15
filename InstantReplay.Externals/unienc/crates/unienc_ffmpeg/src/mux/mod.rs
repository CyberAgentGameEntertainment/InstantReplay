use std::path::Path;

use tokio::io::AsyncWriteExt;
use unienc_common::{CompletionHandle, Muxer, MuxerInput};

use crate::{
    audio::AudioEncodedData,
    error::{FFmpegError, Result},
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

        let mut inputs = ffmpeg.inputs.take().ok_or(FFmpegError::InputsNotAvailable)?;
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
    ) -> unienc_common::Result<(
        Self::VideoInputType,
        Self::AudioInputType,
        Self::CompletionHandleType,
    )> {
        Ok((self.video, self.audio, self.completion))
    }
}

impl MuxerInput for FFmpegMuxerVideoInput {
    type Data = VideoEncodedData;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        let input = self.input.as_mut().ok_or(FFmpegError::InputNotAvailable)?;
        match data {
            VideoEncodedData::ParameterSet(payload) => {
                input.write_all(&payload).await.map_err(|e| FFmpegError::from(e))?;
            }
            VideoEncodedData::Slice { payload, .. } => {
                input.write_all(&payload).await.map_err(|e| FFmpegError::from(e))?;
            }
        }

        input.flush().await.map_err(|e| FFmpegError::from(e))?;

        Ok(())
    }

    async fn finish(mut self) -> unienc_common::Result<()> {
        // take input to drop it to ensure stdin / pipe is closed
        self.input.take().ok_or(FFmpegError::InputNotAvailable)?.shutdown().await.map_err(|e| FFmpegError::from(e))?;
        Ok(())
    }
}

impl MuxerInput for FFmpegMuxerAudioInput {
    type Data = AudioEncodedData;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        let input = self.input.as_mut().ok_or(FFmpegError::InputNotAvailable)?;
        input.write_all(&data.header).await.map_err(|e| FFmpegError::from(e))?;
        input.write_all(&data.payload).await.map_err(|e| FFmpegError::from(e))?;

        input.flush().await.map_err(|e| FFmpegError::from(e))?;

        Ok(())
    }

    async fn finish(mut self) -> unienc_common::Result<()> {
        // take input to drop it to ensure stdin / pipe is closed
        self.input.take().ok_or(FFmpegError::InputNotAvailable)?.shutdown().await.map_err(|e| FFmpegError::from(e))?;
        Ok(())
    }
}

impl CompletionHandle for FFmpegCompletionHandle {
    async fn finish(self) -> unienc_common::Result<()> {
        let result = self.child.wait().await.map_err(|e| FFmpegError::from(e))?;
        println!("FFmpeg exited: {}", result);
        if result.success() {
            Ok(())
        } else {
            Err(FFmpegError::ProcessFailed.into())
        }
    }
}
