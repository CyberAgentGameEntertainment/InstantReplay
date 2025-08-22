use anyhow::Context;
use anyhow::{anyhow, Result};
use std::path::Path;
use std::sync::Arc;
use tokio::sync::mpsc;
use tokio::sync::oneshot;
use unienc_common::{
    AudioEncoderOptions, CompletionHandle, Muxer, MuxerInput, VideoEncoderOptions,
};
use windows::Win32::Media::MediaFoundation::*;
use windows_core::IUnknown;
use windows_core::HSTRING;

use crate::audio::AudioEncodedData;
use crate::common::{Payload, UnsafeSend};
use crate::mft::AsyncCallback;
use crate::mft::MediaEventGeneratorCustom;
use crate::video::VideoEncodedData;
use windows::core::Interface;

enum LazyStream {
    None {
        tx: oneshot::Sender<Result<UnsafeSend<IMFMediaType>>>,
        rx: oneshot::Receiver<Result<Stream>>,
    },
    Some(Result<Stream, Arc<anyhow::Error>>),
}

impl LazyStream {
    pub fn some(&self) -> Option<&Stream> {
        match self {
            LazyStream::None { tx: _, rx: _ } => None,
            LazyStream::Some(stream) => stream.as_ref().ok(),
        }
    }

    pub async fn get(
        &mut self,
        media_type: UnsafeSend<IMFMediaType>,
    ) -> Result<&Stream, Arc<anyhow::Error>> {
        let result = async {
            match std::mem::replace(
                self,
                LazyStream::Some(Err(Arc::new(anyhow!("Failed to get stream")))),
            ) {
                LazyStream::None { tx, rx } => {
                    tx.send(Ok(media_type))
                        .map_err(|_| anyhow!("Failed to send video media type"))?;
                    let stream = rx.await??;
                    Ok(stream)
                }
                LazyStream::Some(stream) => Ok(stream.map_err(|e| anyhow!(e))?),
            }
        }
        .await;

        let result = result.map_err(Arc::new);
        *self = LazyStream::Some(result);
        let LazyStream::Some(result) = self else {
            unreachable!()
        };
        result.as_ref().map_err(|e| e.clone())
    }
}

pub struct MediaFoundationMuxer {
    video_stream: LazyStream,
    audio_stream: LazyStream,
    finish_rx: oneshot::Receiver<Result<()>>,
}

impl MediaFoundationMuxer {
    pub fn new<V: VideoEncoderOptions, A: AudioEncoderOptions>(
        output_path: &Path,
        _video_options: &V,
        _audio_options: &A,
    ) -> Result<Self> {
        let file = UnsafeSend(unsafe {
            MFCreateFile(
                MF_ACCESSMODE_READWRITE,
                MF_OPENMODE_DELETE_IF_EXIST,
                MF_FILEFLAGS_NONE,
                &HSTRING::from(output_path),
            )?
        });

        let (video_type_tx, video_type_rx) = oneshot::channel::<Result<UnsafeSend<IMFMediaType>>>();
        let (audio_type_tx, audio_type_rx) = oneshot::channel::<Result<UnsafeSend<IMFMediaType>>>();
        let (finish_tx, finish_rx) = oneshot::channel::<Result<()>>();

        let (video_stream_tx, video_stream_rx) = oneshot::channel::<Result<Stream>>();
        let (audio_stream_tx, audio_stream_rx) = oneshot::channel::<Result<Stream>>();

        tokio::spawn(async move {
            let video_type = video_type_rx.await??;
            let audio_type = audio_type_rx.await??;

            let sink = unsafe { MFCreateMPEG4MediaSink(&*file, &*video_type, &*audio_type)? };
            assert_eq!(
                unsafe { sink.GetCharacteristics()? } & MEDIASINK_RATELESS,
                MEDIASINK_RATELESS
            );
            let finalizable = sink.cast::<IMFFinalizableMediaSink>().ok();
            let sink_count = unsafe { sink.GetStreamSinkCount()? };
            assert_eq!(sink_count, 2);
            let (video_stream, video_finish_rx) =
                Stream::new(unsafe { sink.GetStreamSinkByIndex(0)? })?;
            let (audio_stream, audio_finish_rx) =
                Stream::new(unsafe { sink.GetStreamSinkByIndex(1)? })?;

            if let Some(finalizable) = finalizable {
                let finalizable = UnsafeSend(finalizable);
                let sink = UnsafeSend(sink.clone());
                tokio::spawn(async move {
                    video_finish_rx.await.unwrap();
                    audio_finish_rx.await.unwrap();

                    let finalizable_clone = UnsafeSend(finalizable.clone());
                    let callback: IMFAsyncCallback = AsyncCallback::new(move |result| unsafe {
                        finalizable_clone.EndFinalize(result.unwrap()).unwrap();
                        sink.Shutdown().unwrap();
                        finish_tx.send(Ok(())).unwrap();
                    })
                    .into();

                    unsafe { finalizable.BeginFinalize(&callback, Option::<&IUnknown>::None)? };

                    Result::<()>::Ok(())
                });
            } else {
                tokio::spawn(async move {
                    video_finish_rx.await.unwrap();
                    audio_finish_rx.await.unwrap();
                    finish_tx.send(Ok(()))
                });
            }

            let presentation_clock = unsafe { MFCreatePresentationClock()? };
            let time_source = unsafe { MFCreateSystemTimeSource()? };
            unsafe { presentation_clock.SetTimeSource(&time_source)? };
            unsafe { sink.SetPresentationClock(&presentation_clock)? };

            unsafe { presentation_clock.Start(0)? };

            video_stream_tx
                .send(Ok(video_stream))
                .map_err(|_| anyhow!("Failed to send video stream"))?;
            audio_stream_tx
                .send(Ok(audio_stream))
                .map_err(|_| anyhow!("Failed to send audio stream"))?;

            Result::<()>::Ok(())
        });

        let video_stream = LazyStream::None {
            tx: video_type_tx,
            rx: video_stream_rx,
        };
        let audio_stream = LazyStream::None {
            tx: audio_type_tx,
            rx: audio_stream_rx,
        };

        Ok(Self {
            video_stream,
            audio_stream,
            finish_rx,
        })
    }
}

struct Stream {
    sample_tx: mpsc::Sender<UnsafeSend<IMFSample>>,
}

impl Stream {
    pub fn new(stream: IMFStreamSink) -> Result<(Self, oneshot::Receiver<()>)> {
        let mut ev_rx = stream.get_events();
        let stream = UnsafeSend(stream);
        let stream_cap = UnsafeSend(stream.clone());

        let (sample_tx, sample_rx) = mpsc::channel::<UnsafeSend<IMFSample>>(32);
        let (finish_tx, finish_rx) = oneshot::channel::<()>();

        tokio::spawn(async move {
            let mut sample_rx = sample_rx;
            let mut finish_tx = Some(finish_tx);
            while let Some(event) = ev_rx.recv().await {
                if let Ok(event) = event {
                    let event_type: u32 = unsafe { event.GetType()? };
                    match MF_EVENT_TYPE(event_type as i32) {
                        MEStreamSinkRequestSample => {
                            if let Some(sample) = sample_rx.recv().await {
                                unsafe { stream_cap.ProcessSample(&*sample)? };
                            } else {
                                unsafe {
                                    stream_cap.PlaceMarker(
                                        MFSTREAMSINK_MARKER_ENDOFSEGMENT,
                                        std::ptr::null(),
                                        std::ptr::null(),
                                    )?
                                };
                                if let Some(finish_tx) = finish_tx.take() {
                                    finish_tx
                                        .send(())
                                        .map_err(|_e| anyhow!("Failed to send finish signal"))?
                                };
                            }
                        }
                        _ => {
                            println!("Unhandled media sink event type: {:?}", event_type);
                        }
                    }
                }
            }

            Result::<()>::Ok(())
        });

        Ok((Self { sample_tx }, finish_rx))
    }
}

impl Muxer for MediaFoundationMuxer {
    type VideoInputType = VideoMuxerInputImpl;
    type AudioInputType = AudioMuxerInputImpl;
    type CompletionHandleType = MuxerCompletionHandleImpl;

    fn get_inputs(
        self,
    ) -> Result<(
        Self::VideoInputType,
        Self::AudioInputType,
        Self::CompletionHandleType,
    )> {
        Ok((
            VideoMuxerInputImpl {
                stream: self.video_stream,
            },
            AudioMuxerInputImpl {
                stream: self.audio_stream,
            },
            MuxerCompletionHandleImpl {
                receiver: self.finish_rx,
            },
        ))
    }
}

pub struct VideoMuxerInputImpl {
    stream: LazyStream,
}

impl MuxerInput for VideoMuxerInputImpl {
    type Data = VideoEncodedData;

    async fn push(&mut self, data: Self::Data) -> Result<()> {
        match data.payload {
            Payload::Format(media_type) => {
                self.stream.get(media_type).await.map_err(|e| anyhow!(e))?;
                Ok(())
            }
            Payload::Sample(sample) => {
                let stream = self.stream.some().context("stream is not initialized")?;
                stream
                    .sample_tx
                    .send(sample)
                    .await
                    .map_err(|e| anyhow!("Failed to send video data to muxer: {}", e))
            }
        }
    }

    async fn finish(self) -> Result<()> {
        drop(self.stream);
        Ok(())
    }
}

pub struct AudioMuxerInputImpl {
    stream: LazyStream,
}

impl MuxerInput for AudioMuxerInputImpl {
    type Data = AudioEncodedData;

    async fn push(&mut self, data: Self::Data) -> Result<()> {
        match data.payload {
            Payload::Format(media_type) => {
                self.stream.get(media_type).await.map_err(|e| anyhow!(e))?;
                Ok(())
            }
            Payload::Sample(sample) => {
                let stream = self.stream.some().context("stream is not initialized")?;
                stream
                    .sample_tx
                    .send(sample)
                    .await
                    .map_err(|e| anyhow!("Failed to send video data to muxer: {}", e))
            }
        }
    }

    async fn finish(self) -> Result<()> {
        drop(self.stream);
        Ok(())
    }
}

pub struct MuxerCompletionHandleImpl {
    receiver: oneshot::Receiver<Result<()>>,
}

impl CompletionHandle for MuxerCompletionHandleImpl {
    async fn finish(self) -> Result<()> {
        self.receiver
            .await
            .map_err(|e| anyhow!("Failed to wait for muxer completion: {}", e))?
    }
}
