use crate::error::{OptionExt, Result, WindowsError};
use std::path::Path;
use tokio::sync::mpsc;
use tokio::sync::oneshot;
use unienc_common::SpawnExt;
use unienc_common::{
    AudioEncoderOptions, CompletionHandle, Muxer, MuxerInput, Runtime, VideoEncoderOptions,
};
use windows::Win32::Media::MediaFoundation::*;
use windows_core::HSTRING;
use windows_core::IUnknown;
use windows_core::PROPVARIANT;

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
    Some(Result<Stream>),
}

impl LazyStream {
    pub fn some(&self) -> Option<&Stream> {
        match self {
            LazyStream::None { tx: _, rx: _ } => None,
            LazyStream::Some(stream) => stream.as_ref().ok(),
        }
    }

    pub async fn get(&mut self, media_type: UnsafeSend<IMFMediaType>) -> Result<()> {
        let result = async {
            match std::mem::replace(self, LazyStream::Some(Err(WindowsError::StreamGetFailed))) {
                LazyStream::None { tx, rx } => {
                    tx.send(Ok(media_type))
                        .map_err(|_| WindowsError::MediaTypeSendFailed)?;
                    let stream = rx.await??;
                    Ok(stream)
                }
                LazyStream::Some(stream) => Ok(stream?),
            }
        }
        .await;

        *self = LazyStream::Some(result);
        let LazyStream::Some(result) = self else {
            unreachable!()
        };
        result.as_ref().map_err(|e| e.clone())?;
        Ok(())
    }
}

pub struct MediaFoundationMuxer {
    video_stream: LazyStream,
    audio_stream: LazyStream,
    finish_rx: oneshot::Receiver<Result<()>>,
}

impl MediaFoundationMuxer {
    pub fn new<V: VideoEncoderOptions, A: AudioEncoderOptions, R: Runtime + 'static>(
        output_path: &Path,
        _video_options: &V,
        _audio_options: &A,
        runtime: &R,
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

        let runtime_clone = runtime.clone();

        runtime.spawn_ret(async move {
            let result: Result<()> = async move {
                let runtime_clone = runtime_clone.clone();

                let video_type = video_type_rx.await??;
                let audio_type = audio_type_rx.await??;

                let sink = UnsafeSend(unsafe {
                    MFCreateMPEG4MediaSink(&*file, &*video_type, &*audio_type)?
                });
                let sink_inner = UnsafeSend(sink.clone());
                let result: Result<()> = async move {
                    let sink = sink_inner;
                    assert_eq!(
                        unsafe { sink.GetCharacteristics()? } & MEDIASINK_RATELESS,
                        MEDIASINK_RATELESS
                    );
                    let finalizable = sink.cast::<IMFFinalizableMediaSink>().ok().map(UnsafeSend);
                    let sink_count = unsafe { sink.GetStreamSinkCount()? };
                    assert_eq!(sink_count, 2);
                    let (video_stream, video_finish_rx) =
                        Stream::new(unsafe { sink.GetStreamSinkByIndex(0)? }, &runtime_clone)?;
                    let (audio_stream, audio_finish_rx) =
                        Stream::new(unsafe { sink.GetStreamSinkByIndex(1)? }, &runtime_clone)?;

                    {
                        let presentation_clock = unsafe { MFCreatePresentationClock()? };
                        let time_source = unsafe { MFCreateSystemTimeSource()? };
                        unsafe { presentation_clock.SetTimeSource(&time_source)? };
                        unsafe { sink.SetPresentationClock(&presentation_clock)? };

                        unsafe { presentation_clock.Start(0)? };
                    }

                    video_stream_tx
                        .send(Ok(video_stream))
                        .map_err(|_| WindowsError::StreamSendFailed)?;
                    audio_stream_tx
                        .send(Ok(audio_stream))
                        .map_err(|_| WindowsError::StreamSendFailed)?;

                    video_finish_rx.await?;
                    audio_finish_rx.await?;

                    if let Some(finalizable) = finalizable {
                        let finalizable = UnsafeSend(finalizable);

                        let finalizable_clone = UnsafeSend(finalizable.clone());
                        let (done_tx, done_rx) = oneshot::channel();

                        {
                            let callback: IMFAsyncCallback =
                                AsyncCallback::new(move |result| unsafe {
                                    let result: windows_core::Result<()> = (move || {
                                        finalizable_clone.EndFinalize(result.ok()?)?;
                                        Ok(())
                                    })(
                                    );
                                    let _ = done_tx.send(result);
                                })
                                .into();

                            unsafe {
                                finalizable.BeginFinalize(&callback, Option::<&IUnknown>::None)
                            }?;
                        }

                        done_rx
                            .await
                            .map_err(|e| WindowsError::Other(e.to_string()))??;

                        let _ = unsafe { sink.Shutdown() };
                    }

                    Result::<()>::Ok(())
                }
                .await;

                // Release the output file even when muxing fails; otherwise the
                // sink keeps the file handle open until the process exits.
                if result.is_err() {
                    let _ = unsafe { sink.Shutdown() };
                }

                result
            }
            .await;

            let _ = finish_tx.send(result);
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

/// Context value attached to the end-of-segment marker. Its value is not used; it only has to be
/// a non-empty PROPVARIANT (see the PlaceMarker call in `Stream::new`).
const END_OF_SEGMENT_MARKER_CONTEXT: u32 = 1;

struct Stream {
    sample_tx: mpsc::Sender<UnsafeSend<IMFSample>>,
}

impl Stream {
    pub fn new(
        stream: IMFStreamSink,
        runtime: &impl Runtime,
    ) -> Result<(Self, oneshot::Receiver<()>)> {
        let stream = UnsafeSend(stream);
        let stream_cap = UnsafeSend(stream.clone());

        let (sample_tx, sample_rx) = mpsc::channel::<UnsafeSend<IMFSample>>(32);
        let (finish_tx, finish_rx) = oneshot::channel::<()>();

        runtime.spawn_ret(async move {
            let mut sample_rx = sample_rx;
            let mut finish_tx = Some(finish_tx);
            let mut end_of_segment_placed = false;
            while let Ok(event) = stream_cap.get_event().await {
                let event_type: u32 = unsafe { event.GetType()? };
                match MF_EVENT_TYPE(event_type as i32) {
                    #[allow(non_upper_case_globals)]
                    MEStreamSinkRequestSample => {
                        // The sink asks for samples ahead of consuming them, so requests are
                        // still queued after the segment has ended. Answering one of those
                        // with a second marker would place it on a sink that may already be
                        // finalizing.
                        if end_of_segment_placed {
                            log::debug!("Ignoring a sample request after end of segment");
                            continue;
                        }
                        if let Some(sample) = sample_rx.recv().await {
                            unsafe { stream_cap.ProcessSample(&*sample)? };
                        } else {
                            end_of_segment_placed = true;

                            // The end-of-segment marker is how the MPEG-4 sink learns that
                            // this stream has ended. Without it the sink keeps interleaving
                            // against this stream: once the other stream runs more than
                            // about 1.5 s past this one's last sample, the sink stops
                            // requesting samples from it, that stream never drains and
                            // finalization never starts.
                            //
                            // Some Windows builds (observed on 26200 with mfmp4srcsnk.dll
                            // 10.0.26100.8457 and later) reject PlaceMarker with
                            // MF_E_INVALIDTYPE when pvarContextValue is NULL or VT_EMPTY,
                            // although the documentation allows NULL. Passing a non-empty
                            // context value makes the marker accepted on those builds.
                            let context = PROPVARIANT::from(END_OF_SEGMENT_MARKER_CONTEXT);
                            if let Err(e) = unsafe {
                                stream_cap.PlaceMarker(
                                    MFSTREAMSINK_MARKER_ENDOFSEGMENT,
                                    std::ptr::null(),
                                    &context,
                                )
                            } {
                                // No MEStreamSinkMarker follows a rejected marker, so report
                                // the stream drained on the strength of ProcessSample having
                                // returned for every sample, as before.
                                log::warn!("PlaceMarker(ENDOFSEGMENT) failed (non-fatal): {:?}", e);
                                Self::report_drained(&mut finish_tx)?;
                            }
                        }
                    }
                    #[allow(non_upper_case_globals)]
                    MEStreamSinkMarker => {
                        // The only marker this loop places is the end-of-segment one, and the
                        // sink sends this once it has consumed every sample placed before it:
                        // only from here is it safe to start finalization, which writes the
                        // moov box over data the sink must already be done with.
                        Self::report_drained(&mut finish_tx)?;
                    }
                    _ => {
                        log::debug!("Unhandled media sink event type: {:?}", event_type);
                    }
                }
            }

            Result::<()>::Ok(())
        });

        Ok((Self { sample_tx }, finish_rx))
    }

    /// Tells the muxer task this stream is done with, once.
    fn report_drained(finish_tx: &mut Option<oneshot::Sender<()>>) -> Result<()> {
        if let Some(finish_tx) = finish_tx.take() {
            finish_tx
                .send(())
                .map_err(|_e| WindowsError::FinishSignalSendFailed)?;
        }
        Ok(())
    }
}

impl Muxer for MediaFoundationMuxer {
    type VideoInputType = VideoMuxerInputImpl;
    type AudioInputType = AudioMuxerInputImpl;
    type CompletionHandleType = MuxerCompletionHandleImpl;

    fn get_inputs(
        self,
    ) -> unienc_common::Result<(
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

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        match data.payload {
            Payload::Format(media_type) => {
                self.stream
                    .get(media_type)
                    .await
                    .map_err(|e| WindowsError::Other(e.to_string()))?;
                Ok(())
            }
            Payload::Sample(sample) => {
                let stream = self
                    .stream
                    .some()
                    .ok_or(WindowsError::StreamNotInitialized)?;
                stream
                    .sample_tx
                    .send(sample)
                    .await
                    .map_err(|e| WindowsError::MuxerSendFailed(e.to_string()))?;
                Ok(())
            }
        }
    }

    async fn finish(self) -> unienc_common::Result<()> {
        drop(self.stream);
        Ok(())
    }
}

pub struct AudioMuxerInputImpl {
    stream: LazyStream,
}

impl MuxerInput for AudioMuxerInputImpl {
    type Data = AudioEncodedData;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        match data.payload {
            Payload::Format(media_type) => {
                self.stream
                    .get(media_type)
                    .await
                    .map_err(|e| WindowsError::Other(e.to_string()))?;
                Ok(())
            }
            Payload::Sample(sample) => {
                let stream = self
                    .stream
                    .some()
                    .ok_or(WindowsError::StreamNotInitialized)?;
                stream
                    .sample_tx
                    .send(sample)
                    .await
                    .map_err(|e| WindowsError::MuxerSendFailed(e.to_string()))?;
                Ok(())
            }
        }
    }

    async fn finish(self) -> unienc_common::Result<()> {
        drop(self.stream);
        Ok(())
    }
}

pub struct MuxerCompletionHandleImpl {
    receiver: oneshot::Receiver<Result<()>>,
}

impl CompletionHandle for MuxerCompletionHandleImpl {
    async fn finish(self) -> unienc_common::Result<()> {
        self.receiver
            .await
            .map_err(|e| WindowsError::MuxerCompletionWaitFailed(e.to_string()))?
            .map_err(|e| e.into())
    }
}
