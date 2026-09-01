use crate::error::{Result, WindowsError};
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

use crate::audio::AudioEncodedData;
use crate::common::{ErrorSlot, Payload, UnsafeSend};
use crate::mft::AsyncCallback;
use crate::mft::MediaEventGeneratorCustom;
use crate::video::VideoEncodedData;
use windows::core::{GUID, Interface};

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

/// Renders a media subtype the way its documentation names it.
///
/// Media Foundation builds most subtype GUIDs from a fourcc (video) or a WAVE
/// format tag (audio) in the first field, over a fixed suffix. Printing the
/// whole GUID leaves the reader of a log to decode it by hand.
fn describe_subtype(guid: GUID) -> String {
    const MEDIASUBTYPE_SUFFIX: (u16, u16, [u8; 8]) = (
        0x0000,
        0x0010,
        [0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71],
    );

    if (guid.data2, guid.data3, guid.data4) != MEDIASUBTYPE_SUFFIX {
        return format!("{guid:?}");
    }

    let fourcc = guid.data1.to_le_bytes();
    if fourcc.iter().all(|b| b.is_ascii_graphic()) {
        String::from_utf8_lossy(&fourcc).into_owned()
    } else {
        // An audio subtype: the field holds a WAVE format tag, not a fourcc.
        format!("format tag {:#06x}", guid.data1)
    }
}

/// Names the stream sink events an export can see, so that a log of one that
/// went wrong does not read as a list of numbers.
fn describe_sink_event(event_type: u32) -> &'static str {
    #[allow(non_upper_case_globals)]
    match MF_EVENT_TYPE(event_type as i32) {
        MEError => "MEError",
        MEStreamSinkStarted => "MEStreamSinkStarted",
        MEStreamSinkStopped => "MEStreamSinkStopped",
        MEStreamSinkPaused => "MEStreamSinkPaused",
        MEStreamSinkRateChanged => "MEStreamSinkRateChanged",
        MEStreamSinkRequestSample => "MEStreamSinkRequestSample",
        MEStreamSinkMarker => "MEStreamSinkMarker",
        MEStreamSinkPrerolled => "MEStreamSinkPrerolled",
        MEStreamSinkFormatChanged => "MEStreamSinkFormatChanged",
        _ => "unrecognized event",
    }
}

/// Summarises the media type a track is described with, for the log.
///
/// The MPEG-4 sink builds the track's `moov` entry out of this and cannot be
/// told anything different later, so when an output turns out to be unplayable
/// this is the description it was written against.
fn describe_media_type(media_type: &IMFMediaType) -> String {
    let subtype = unsafe { media_type.GetGUID(&MF_MT_SUBTYPE) }
        .map(describe_subtype)
        .unwrap_or_else(|_| "unknown subtype".into());

    // Present once the encoder has published its parameter sets (SPS/PPS for
    // H.264). A track described without them depends on the sink recovering
    // them from the bitstream instead.
    let sequence_header = unsafe { media_type.GetBlobSize(&MF_MT_MPEG_SEQUENCE_HEADER) }
        .map(|size| format!("{size} byte sequence header"))
        .unwrap_or_else(|_| "no sequence header".into());

    match unsafe { media_type.GetUINT64(&MF_MT_FRAME_SIZE) } {
        Ok(frame_size) => format!(
            "{subtype} {}x{}, {sequence_header}",
            frame_size >> 32,
            frame_size & 0xffff_ffff
        ),
        Err(_) => format!("{subtype}, {sequence_header}"),
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
        // The sink writes `mdat` straight into this file as samples arrive and
        // then seeks back to write `moov` during finalization. Naming the file
        // is what lets a report about a truncated or unplayable output be tied
        // to the export that produced it.
        log::info!("Opening {} for muxing", output_path.display());
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
            let mut video_stream_tx = Some(video_stream_tx);
            let mut audio_stream_tx = Some(audio_stream_tx);

            let result = Self::mux(
                file,
                runtime_clone,
                video_type_rx,
                audio_type_rx,
                &mut video_stream_tx,
                &mut audio_stream_tx,
            )
            .await;

            // A caller blocked in `LazyStream::get` would otherwise see nothing
            // but a dropped sender. Hand it the failure that actually happened.
            if let Err(e) = &result {
                for tx in [video_stream_tx.take(), audio_stream_tx.take()]
                    .into_iter()
                    .flatten()
                {
                    let _ = tx.send(Err(e.clone()));
                }
            }

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

    /// Drives the MPEG-4 media sink for one output file, from the media types
    /// the encoders announce through to the finalized `moov`.
    async fn mux<R: Runtime + 'static>(
        file: UnsafeSend<IMFByteStream>,
        runtime: R,
        video_type_rx: oneshot::Receiver<Result<UnsafeSend<IMFMediaType>>>,
        audio_type_rx: oneshot::Receiver<Result<UnsafeSend<IMFMediaType>>>,
        video_stream_tx: &mut Option<oneshot::Sender<Result<Stream>>>,
        audio_stream_tx: &mut Option<oneshot::Sender<Result<Stream>>>,
    ) -> Result<()> {
        let video_type = video_type_rx.await??;
        let audio_type = audio_type_rx.await??;

        log::debug!("Muxing video as {}", describe_media_type(&video_type));
        log::debug!("Muxing audio as {}", describe_media_type(&audio_type));

        let sink =
            UnsafeSend(unsafe { MFCreateMPEG4MediaSink(&*file, &*video_type, &*audio_type)? });

        let result = Self::run_sink(&sink, runtime, video_stream_tx, audio_stream_tx).await;

        // Release the output file even when muxing fails; otherwise the
        // sink keeps the file handle open until the process exits.
        if result.is_err() {
            let _ = unsafe { sink.Shutdown() };
        }

        result
    }

    async fn run_sink<R: Runtime + 'static>(
        sink: &UnsafeSend<IMFMediaSink>,
        runtime: R,
        video_stream_tx: &mut Option<oneshot::Sender<Result<Stream>>>,
        audio_stream_tx: &mut Option<oneshot::Sender<Result<Stream>>>,
    ) -> Result<()> {
        assert_eq!(
            unsafe { sink.GetCharacteristics()? } & MEDIASINK_RATELESS,
            MEDIASINK_RATELESS
        );
        let finalizable = sink.cast::<IMFFinalizableMediaSink>().ok().map(UnsafeSend);
        if finalizable.is_none() {
            // Without finalization there is no `moov` box, and the file this
            // export produces will not play anywhere.
            log::warn!("Media sink is not finalizable; the output will have no moov box");
        }
        let sink_count = unsafe { sink.GetStreamSinkCount()? };
        assert_eq!(sink_count, 2);
        let (video_stream, video_finish_rx) =
            Stream::new("Video", unsafe { sink.GetStreamSinkByIndex(0)? }, &runtime)?;
        let (audio_stream, audio_finish_rx) =
            Stream::new("Audio", unsafe { sink.GetStreamSinkByIndex(1)? }, &runtime)?;

        {
            let presentation_clock = unsafe { MFCreatePresentationClock()? };
            let time_source = unsafe { MFCreateSystemTimeSource()? };
            unsafe { presentation_clock.SetTimeSource(&time_source)? };
            unsafe { sink.SetPresentationClock(&presentation_clock)? };

            unsafe { presentation_clock.Start(0)? };
        }

        video_stream_tx
            .take()
            .ok_or(WindowsError::StreamSendFailed)?
            .send(Ok(video_stream))
            .map_err(|_| WindowsError::StreamSendFailed)?;
        audio_stream_tx
            .take()
            .ok_or(WindowsError::StreamSendFailed)?
            .send(Ok(audio_stream))
            .map_err(|_| WindowsError::StreamSendFailed)?;

        video_finish_rx.await??;
        audio_finish_rx.await??;

        if let Some(finalizable) = finalizable {
            let finalizable = UnsafeSend(finalizable);

            let finalizable_clone = UnsafeSend(finalizable.clone());
            let (done_tx, done_rx) = oneshot::channel();

            {
                let callback: IMFAsyncCallback = AsyncCallback::new(move |result| unsafe {
                    let result: windows_core::Result<()> = (move || {
                        finalizable_clone.EndFinalize(result.ok()?)?;
                        Ok(())
                    })();
                    let _ = done_tx.send(result);
                })
                .into();

                // Both streams have drained, so everything left is the `moov`
                // box. An export that stops between these two lines is stuck in
                // the OS sink, not in anything upstream of it.
                log::info!("Finalizing media sink");
                unsafe { finalizable.BeginFinalize(&callback, Option::<&IUnknown>::None) }?;
            }

            done_rx
                .await
                .map_err(|_| WindowsError::FinalizeResultLost)??;
            log::info!("Media sink finalized");

            let _ = unsafe { sink.Shutdown() };
        }

        Ok(())
    }
}

struct Stream {
    sample_tx: mpsc::Sender<UnsafeSend<IMFSample>>,
    errors: ErrorSlot,
}

impl Stream {
    /// Hands a sample to the sink, reporting why the sink stopped listening
    /// rather than just that it did.
    async fn push(&self, sample: UnsafeSend<IMFSample>) -> Result<()> {
        self.sample_tx.send(sample).await.map_err(|e| {
            self.errors
                .get_or(WindowsError::MuxerSendFailed(e.to_string()))
        })
    }

    pub fn new(
        kind: &'static str,
        stream: IMFStreamSink,
        runtime: &impl Runtime,
    ) -> Result<(Self, oneshot::Receiver<Result<()>>)> {
        let stream = UnsafeSend(stream);
        let stream_cap = UnsafeSend(stream.clone());

        let (sample_tx, sample_rx) = mpsc::channel::<UnsafeSend<IMFSample>>(32);
        let (finish_tx, finish_rx) = oneshot::channel::<Result<()>>();
        let errors = ErrorSlot::default();
        let loop_errors = errors.clone();

        runtime.spawn_ret(async move {
            let mut sample_rx = sample_rx;
            let mut finish_tx = Some(finish_tx);

            let result = Self::pump(kind, &stream_cap, &mut sample_rx, &mut finish_tx).await;

            // The muxer task learns of this loop only through `finish_tx`. If it
            // is still waiting, the loop's error is the export's error and has
            // to travel down that channel; dropping the sender instead is what
            // used to surface as a bare "channel closed" with the HRESULT lost.
            if let Err(e) = &result {
                log::error!("{kind} media sink stream loop failed: {e:?}");
                loop_errors.set(e.clone());
                if let Some(finish_tx) = finish_tx.take() {
                    let _ = finish_tx.send(Err(e.clone()));
                }
            }

            result
        });

        Ok((Self { sample_tx, errors }, finish_rx))
    }

    /// Feeds the stream sink until it has taken everything, then reports that
    /// the stream drained through `finish_tx`.
    ///
    /// Once there is nothing left to send, the stream is told so with an
    /// end-of-segment marker, and the sink answers with `MEStreamSinkMarker`
    /// after it has consumed everything placed before it. That answer is what
    /// `finish_tx` reports, and it is the only thing that makes finalization
    /// safe to start: finalization writes the `moov` box over a stream the sink
    /// must already be done with.
    async fn pump(
        kind: &'static str,
        stream: &UnsafeSend<IMFStreamSink>,
        sample_rx: &mut mpsc::Receiver<UnsafeSend<IMFSample>>,
        finish_tx: &mut Option<oneshot::Sender<Result<()>>>,
    ) -> Result<()> {
        let mut accepted = 0u64;
        let mut end_of_segment_placed = false;

        loop {
            let event = match stream.get_event().await {
                Ok(event) => event,
                Err(e) => {
                    // Shutting the sink down stops its event generator, which is
                    // how this loop is meant to end — but only once the muxer
                    // task has been told the stream drained. Before that, losing
                    // the event stream is a failure like any other.
                    return if finish_tx.is_none() { Ok(()) } else { Err(e) };
                }
            };

            let event_type: u32 = unsafe { event.GetType()? };
            match MF_EVENT_TYPE(event_type as i32) {
                #[allow(non_upper_case_globals)]
                MEStreamSinkRequestSample => {
                    // The sink asks for samples ahead of consuming them, so
                    // requests are still queued after the segment has ended.
                    // Answering one of those with a second marker would place it
                    // on a sink that may already be finalizing.
                    if end_of_segment_placed {
                        log::debug!(
                            "Ignoring a sample request on the {kind} stream after end of segment"
                        );
                        continue;
                    }

                    if let Some(sample) = sample_rx.recv().await {
                        unsafe { stream.ProcessSample(&*sample)? };
                        accepted += 1;
                        continue;
                    }

                    end_of_segment_placed = true;

                    if let Err(e) = unsafe {
                        stream.PlaceMarker(
                            MFSTREAMSINK_MARKER_ENDOFSEGMENT,
                            std::ptr::null(),
                            std::ptr::null(),
                        )
                    } {
                        // Some Windows builds (observed on 26200 with
                        // mfmp4srcsnk.dll 10.0.26100.8457) reject PlaceMarker
                        // with MF_E_INVALIDTYPE. No MEStreamSinkMarker will
                        // follow, so report the stream drained on the strength of
                        // ProcessSample having returned for every sample, and let
                        // finalization run rather than failing the export.
                        log::warn!("{kind} PlaceMarker(ENDOFSEGMENT) failed (non-fatal): {e:?}");
                        Self::report_drained(kind, accepted, finish_tx)?;
                    }
                }
                #[allow(non_upper_case_globals)]
                MEStreamSinkMarker => {
                    // The only marker this loop places is the end-of-segment one,
                    // so this says the sink has taken everything.
                    Self::report_drained(kind, accepted, finish_tx)?;
                }
                _ => {
                    log::debug!(
                        "Ignoring {kind} sink event {} ({event_type})",
                        describe_sink_event(event_type)
                    );
                }
            }
        }
    }

    /// Tells the muxer task this stream is done with, once.
    fn report_drained(
        kind: &'static str,
        accepted: u64,
        finish_tx: &mut Option<oneshot::Sender<Result<()>>>,
    ) -> Result<()> {
        if let Some(finish_tx) = finish_tx.take() {
            // How much actually reached the sink separates "the export wrote
            // nothing" from "the export wrote everything and then failed to
            // finalize".
            log::info!("{kind} stream drained after {accepted} samples");
            finish_tx
                .send(Ok(()))
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
                stream.push(sample).await?;
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
                stream.push(sample).await?;
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
            .map_err(|_| {
                WindowsError::MuxerCompletionWaitFailed(
                    "the muxer task ended without reporting a result".into(),
                )
            })?
            .map_err(|e| e.into())
    }
}
