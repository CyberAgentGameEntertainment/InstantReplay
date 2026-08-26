use crate::audio::AudioEncodedData;
use crate::js::make_download;
use crate::video::VideoEncodedData;
use futures::channel::oneshot;
use futures::join;
use muxide::api::{AacProfile, AudioCodec, MuxerBuilder, VideoCodec};
use std::io::Write;
use std::sync::{Arc, Mutex};
use unienc_common::{
    CommonError, CompletionHandle, EncodedData, Muxer, MuxerInput, OptionExt, ResultExt,
};

#[derive(Clone)]
struct FragmentWrite {
    inner: Arc<Mutex<Vec<Vec<u8>>>>,
}

impl FragmentWrite {
    fn new() -> Self {
        Self {
            inner: Arc::new(Mutex::new(Vec::new())),
        }
    }

    fn with_ref(&self, f: impl FnOnce(&[Vec<u8>])) {
        let inner_guard = self.inner.lock().unwrap();
        f(&inner_guard);
    }
}

impl Write for FragmentWrite {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        let mut inner_guard = self.inner.lock().unwrap();
        inner_guard.push(buf.to_vec());
        Ok(buf.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

/// The muxer, together with the audio it is not ready for yet.
///
/// muxide refuses audio until a video frame has been written, and the pipeline
/// feeds both streams concurrently, so which one produces output first is up to
/// the encoders rather than up to us. Audio that arrives early is held here and
/// written as soon as the first video frame lands, which keeps the ordering
/// requirement inside the muxer instead of imposing it on every caller.
///
/// The backlog only spans the gap before the first video frame, which is a frame
/// interval in the ordinary case.
struct PendingMuxer {
    /// `None` once the muxer has been finished.
    muxer: Option<muxide::api::Muxer<FragmentWrite>>,
    /// Audio waiting for a video frame to make it acceptable.
    pending_audio: Vec<AudioEncodedData>,
    video_written: bool,
}

impl PendingMuxer {
    fn new(muxer: muxide::api::Muxer<FragmentWrite>) -> Self {
        Self {
            muxer: Some(muxer),
            pending_audio: Vec::new(),
            video_written: false,
        }
    }

    fn get(&mut self) -> unienc_common::Result<&mut muxide::api::Muxer<FragmentWrite>> {
        self.muxer.as_mut().context("The muxer is already finished")
    }

    fn write_video(&mut self, data: &VideoEncodedData) -> unienc_common::Result<()> {
        self.get()?
            .write_video(data.timestamp(), &data.data, data.is_key)
            .context("Failed to write encoded frame")?;
        self.video_written = true;

        // Anything held back is now acceptable, and goes in before any later
        // audio so that the stream stays in order.
        for data in std::mem::take(&mut self.pending_audio) {
            self.get()?
                .write_audio(data.timestamp(), &data.data)
                .context("Failed to write held back audio frame")?;
        }
        Ok(())
    }

    fn write_audio(&mut self, data: AudioEncodedData) -> unienc_common::Result<()> {
        if !self.video_written {
            self.pending_audio.push(data);
            return Ok(());
        }
        self.get()?
            .write_audio(data.timestamp(), &data.data)
            .context("Failed to write encoded frame")?;
        Ok(())
    }
}

pub struct WebCodecsMuxer {
    video: WebCodecsVideoInput,
    audio: WebCodecsAudioInput,
    completion: WebCodecsCompletionHandle,
}
pub struct WebCodecsVideoInput {
    muxer: Arc<Mutex<PendingMuxer>>,
    finish_tx: Option<oneshot::Sender<()>>,
}
pub struct WebCodecsAudioInput {
    muxer: Arc<Mutex<PendingMuxer>>,
    finish_tx: Option<oneshot::Sender<()>>,
}
pub struct WebCodecsCompletionHandle {
    filename: String,
    writer: FragmentWrite,
    muxer: Arc<Mutex<PendingMuxer>>,
    video_finish_rx: Option<oneshot::Receiver<()>>,
    audio_finish_rx: Option<oneshot::Receiver<()>>,
}

impl WebCodecsMuxer {
    pub fn new<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions>(
        output_path: &std::path::Path,
        video_options: &V,
        audio_options: &A,
    ) -> unienc_common::Result<Self> {
        let writer = FragmentWrite::new();
        let filename = output_path
            .file_name()
            .context("Output path has no filename")?
            .to_string_lossy()
            .to_string();

        let muxer = Arc::new(Mutex::new(PendingMuxer::new(
            MuxerBuilder::new(writer.clone())
                .video(
                    VideoCodec::H264,
                    video_options.width(),
                    video_options.height(),
                    video_options.fps_hint() as f64,
                )
                .audio(
                    AudioCodec::Aac(AacProfile::Lc),
                    audio_options.sample_rate(),
                    audio_options.channels() as u16,
                )
                .with_fast_start(true)
                .build()
                .context("Failed to create muxer")?,
        )));

        let (video_finish_tx, video_finish_rx) = oneshot::channel();
        let (audio_finish_tx, audio_finish_rx) = oneshot::channel();

        Ok(Self {
            video: WebCodecsVideoInput {
                muxer: muxer.clone(),
                finish_tx: video_finish_tx.into(),
            },
            audio: WebCodecsAudioInput {
                muxer: muxer.clone(),
                finish_tx: audio_finish_tx.into(),
            },
            completion: WebCodecsCompletionHandle {
                filename,
                writer,
                muxer,
                video_finish_rx: video_finish_rx.into(),
                audio_finish_rx: audio_finish_rx.into(),
            },
        })
    }
}

impl Muxer for WebCodecsMuxer {
    type VideoInputType = WebCodecsVideoInput;
    type AudioInputType = WebCodecsAudioInput;
    type CompletionHandleType = WebCodecsCompletionHandle;

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

impl MuxerInput for WebCodecsVideoInput {
    type Data = VideoEncodedData;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        self.muxer.lock().unwrap().write_video(&data)
    }

    async fn finish(mut self) -> unienc_common::Result<()> {
        self.finish_tx
            .take()
            .unwrap()
            .send(())
            .map_err(|e| CommonError::Other(format!("Failed to finish video: {:?}", e)))?;
        Ok(())
    }
}

impl MuxerInput for WebCodecsAudioInput {
    type Data = AudioEncodedData;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        self.muxer.lock().unwrap().write_audio(data)
    }

    async fn finish(mut self) -> unienc_common::Result<()> {
        self.finish_tx
            .take()
            .unwrap()
            .send(())
            .map_err(|e| CommonError::Other(format!("Failed to finish video: {:?}", e)))?;
        Ok(())
    }
}

impl CompletionHandle for WebCodecsCompletionHandle {
    async fn finish(mut self) -> unienc_common::Result<()> {
        join!(
            self.video_finish_rx.take().unwrap(),
            self.audio_finish_rx.take().unwrap()
        );
        let mut muxer_guard = self.muxer.lock().unwrap();
        // Held back audio can only be written after a video frame, so if any is
        // still waiting there never was one and the output would be silent
        // without saying so.
        if !muxer_guard.pending_audio.is_empty() {
            return Err(CommonError::Other(format!(
                "no video frame was ever written, so {} audio frame(s) could not be muxed",
                muxer_guard.pending_audio.len()
            )));
        }
        let muxer = muxer_guard
            .muxer
            .take()
            .context("The muxer is already finished")?;
        muxer.finish().context("Failed to finish audio")?;

        self.writer
            .with_ref(|fragments| make_download(fragments, "video/mp4", &self.filename));

        Ok(())
    }
}
