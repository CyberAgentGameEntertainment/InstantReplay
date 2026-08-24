use std::io::Write;
use std::path::Path;

use unienc_common::{CompletionHandle, Muxer, MuxerInput, Runtime};

use crate::{
    audio::AudioEncodedData,
    error::{FFmpegError, Result},
    ffmpeg::{self, FFmpeg},
    video::VideoEncodedData,
};

pub struct FFmpegMuxer<R: Runtime> {
    video: FFmpegMuxerVideoInput<R>,
    audio: FFmpegMuxerAudioInput<R>,
    completion: FFmpegCompletionHandle<R>,
}

pub struct FFmpegCompletionHandle<R: Runtime> {
    child: FFmpeg,
    runtime: R,
}

pub struct FFmpegMuxerVideoInput<R: Runtime> {
    input: Option<ffmpeg::Input<R>>,
}

pub struct FFmpegMuxerAudioInput<R: Runtime> {
    input: Option<ffmpeg::Input<R>>,
}

impl<R: Runtime + 'static> FFmpegMuxer<R> {
    pub fn new<P: AsRef<Path>>(
        output_path: P,
        video_options: &impl unienc_common::VideoEncoderOptions,
        audio_options: &impl unienc_common::AudioEncoderOptions,
        runtime: R,
    ) -> Result<Self> {
        // raw H.264 frame cannot have timestamp, so we need to assume CFR (encoder also supports CFR)
        let mut spawned = ffmpeg::Builder::new()
            .use_stdin(true)
            .input(["-f", "h264", "-r", &format!("{}", video_options.fps_hint())])
            .input(["-f", "aac"])
            .build(
                [
                    "-pix_fmt",
                    "yuv420p",
                    "-c:v",
                    "copy",
                    "-c:a",
                    "copy",
                    // The raw H.264 stream carries no timestamps at all, so
                    // FFmpeg synthesizes them while muxing. Without this the
                    // default handling of the resulting negative start time
                    // discards the last frames of the video track instead of
                    // shifting the timeline, which left the video shorter than
                    // the audio.
                    "-avoid_negative_ts",
                    "make_zero",
                    "-f",
                    "mp4",
                ],
                ffmpeg::Destination::Path(output_path.as_ref().as_os_str().to_owned()),
            )?;

        if spawned.inputs.len() < 2 {
            return Err(FFmpegError::InputsNotAvailable);
        }
        let audio_input = spawned.inputs.remove(1);
        let video_input = spawned.inputs.remove(0);

        Ok(FFmpegMuxer {
            video: FFmpegMuxerVideoInput {
                input: Some(ffmpeg::Input::new(runtime.clone(), video_input)),
            },
            audio: FFmpegMuxerAudioInput {
                input: Some(ffmpeg::Input::new(runtime.clone(), audio_input)),
            },
            completion: FFmpegCompletionHandle {
                child: spawned.ffmpeg,
                runtime,
            },
        })
    }
}

impl<R: Runtime + 'static> Muxer for FFmpegMuxer<R> {
    type VideoInputType = FFmpegMuxerVideoInput<R>;
    type AudioInputType = FFmpegMuxerAudioInput<R>;
    type CompletionHandleType = FFmpegCompletionHandle<R>;

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

impl<R: Runtime + 'static> MuxerInput for FFmpegMuxerVideoInput<R> {
    type Data = VideoEncodedData;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        let input = self.input.as_mut().ok_or(FFmpegError::InputNotAvailable)?;
        let payload = match data {
            VideoEncodedData::ParameterSet(payload) => payload,
            VideoEncodedData::Slice { payload, .. } => payload,
        };

        input
            .with_writer(move |writer| {
                writer.write_all(&payload)?;
                writer.flush()
            })
            .await?;

        Ok(())
    }

    async fn finish(mut self) -> unienc_common::Result<()> {
        // take input to drop it to ensure stdin / pipe is closed
        self.input
            .take()
            .ok_or(FFmpegError::InputNotAvailable)?
            .close()
            .await?;
        Ok(())
    }
}

impl<R: Runtime + 'static> MuxerInput for FFmpegMuxerAudioInput<R> {
    type Data = AudioEncodedData;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        let input = self.input.as_mut().ok_or(FFmpegError::InputNotAvailable)?;
        input
            .with_writer(move |writer| {
                writer.write_all(&data.header)?;
                writer.write_all(&data.payload)?;
                writer.flush()
            })
            .await?;

        Ok(())
    }

    async fn finish(mut self) -> unienc_common::Result<()> {
        // take input to drop it to ensure stdin / pipe is closed
        self.input
            .take()
            .ok_or(FFmpegError::InputNotAvailable)?
            .close()
            .await?;
        Ok(())
    }
}

impl<R: Runtime + 'static> CompletionHandle for FFmpegCompletionHandle<R> {
    async fn finish(self) -> unienc_common::Result<()> {
        let FFmpegCompletionHandle { child, runtime } = self;
        let result = child.wait(runtime).await?;
        println!("FFmpeg exited: {}", result);
        if result.success() {
            Ok(())
        } else {
            Err(FFmpegError::ProcessFailed.into())
        }
    }
}
