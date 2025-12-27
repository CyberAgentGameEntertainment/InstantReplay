use crate::audio::AudioEncodedData;
use crate::js::make_download;
use crate::video::VideoEncodedData;
use futures::channel::oneshot;
use futures::{join, StreamExt};
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

pub struct WebCodecsMuxer {
    video: WebCodecsVideoInput,
    audio: WebCodecsAudioInput,
    completion: WebCodecsCompletionHandle,
}
pub struct WebCodecsVideoInput {
    muxer: Arc<Mutex<Option<muxide::api::Muxer<FragmentWrite>>>>,
    finish_tx: Option<oneshot::Sender<()>>,
}
pub struct WebCodecsAudioInput {
    muxer: Arc<Mutex<Option<muxide::api::Muxer<FragmentWrite>>>>,
    finish_tx: Option<oneshot::Sender<()>>,
}
pub struct WebCodecsCompletionHandle {
    filename: String,
    writer: FragmentWrite,
    muxer: Arc<Mutex<Option<muxide::api::Muxer<FragmentWrite>>>>,
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

        let muxer = Arc::new(Mutex::new(Some(
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
        let mut muxer_guard = self.muxer.lock().unwrap();
        let muxer = muxer_guard.as_mut().unwrap();
        muxer
            .write_video(data.timestamp(), &data.data, data.is_key)
            .context("Failed to write encoded frame")?;
        Ok(())
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
        let mut muxer_guard = self.muxer.lock().unwrap();
        let muxer = muxer_guard.as_mut().unwrap();
        muxer
            .write_audio(data.timestamp(), &data.data)
            .context("Failed to write encoded frame")?;
        Ok(())
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
        let muxer = muxer_guard.take().unwrap();
        muxer.finish().context("Failed to finish audio")?;

        self.writer
            .with_ref(|fragments| make_download(fragments, "video/mp4", &self.filename));

        Ok(())
    }
}
