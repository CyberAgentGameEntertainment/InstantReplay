use jni::{JNIEnv, sys::jint};
use std::{path::Path, sync::Arc};
use tokio::sync::{RwLock, oneshot};
use unienc_common::{CompletionHandle, Muxer, MuxerInput};

use crate::bindings;
use crate::common::*;
use crate::config::{
    COLOR_RANGE_LIMITED, COLOR_STANDARD_BT709, COLOR_TRANSFER_SDR_VIDEO,
    MUXER_OUTPUT_FORMAT_MPEG_4, format_keys,
};
use crate::error::{AndroidError, Result};
use crate::java::*;

pub struct MediaMuxer {
    video_input: MediaMuxerVideoInput,
    audio_input: MediaMuxerAudioInput,
    completion_handle: MediaMuxerCompletionHandle,
}

enum MuxerSharedState {
    None,
    Partial(oneshot::Sender<Result<()>>), // either video or audio has started (sender is used to signal the other side to start)
    Started,                              // both video and audio have started
}

pub struct MediaMuxerVideoInput {
    muxer: SafeGlobalRef,
    shared_state: Arc<RwLock<MuxerSharedState>>,
    finish_tx: oneshot::Sender<Result<()>>,
    video_track_index: Option<jint>,
    original_width: u32,
    original_height: u32,
}

pub struct MediaMuxerAudioInput {
    muxer: SafeGlobalRef,
    shared_state: Arc<RwLock<MuxerSharedState>>,
    finish_tx: oneshot::Sender<Result<()>>,
    audio_track_index: Option<jint>,
}

pub struct MediaMuxerCompletionHandle {
    video_finish_rx: oneshot::Receiver<Result<()>>,
    audio_finish_rx: oneshot::Receiver<Result<()>>,
    shared_state: Arc<RwLock<MuxerSharedState>>,
    muxer: SafeGlobalRef,
}

impl Muxer for MediaMuxer {
    type VideoInputType = MediaMuxerVideoInput;
    type AudioInputType = MediaMuxerAudioInput;
    type CompletionHandleType = MediaMuxerCompletionHandle;

    fn get_inputs(
        self,
    ) -> unienc_common::Result<(
        Self::VideoInputType,
        Self::AudioInputType,
        Self::CompletionHandleType,
    )> {
        Ok((self.video_input, self.audio_input, self.completion_handle))
    }
}

impl MediaMuxer {
    pub fn new<V: unienc_common::VideoEncoderOptions, A: unienc_common::AudioEncoderOptions>(
        output_path: &Path,
        _video_options: &V,
        _audio_options: &A,
    ) -> Result<Self> {
        let env = &mut attach_current_thread()?;

        // Create MediaMuxer
        let muxer = create_media_muxer(env, output_path)?;

        let (video_finish_tx, video_finish_rx) = oneshot::channel();
        let (audio_finish_tx, audio_finish_rx) = oneshot::channel();

        let shared_state = Arc::new(RwLock::new(MuxerSharedState::None));

        Ok(Self {
            video_input: MediaMuxerVideoInput {
                muxer: muxer.clone(),
                shared_state: shared_state.clone(),
                finish_tx: video_finish_tx,
                video_track_index: None,
                original_width: _video_options.width(),
                original_height: _video_options.height(),
            },
            audio_input: MediaMuxerAudioInput {
                muxer: muxer.clone(),
                shared_state: shared_state.clone(),
                finish_tx: audio_finish_tx,
                audio_track_index: None,
            },
            completion_handle: MediaMuxerCompletionHandle {
                video_finish_rx,
                audio_finish_rx,
                shared_state,
                muxer,
            },
        })
    }
}

async fn push(
    data: CommonEncodedData,
    shared_state: Arc<RwLock<MuxerSharedState>>,
    muxer: &SafeGlobalRef,
    track_index: &mut Option<jint>,
    original_width: Option<u32>,
    original_height: Option<u32>,
) -> Result<()> {
    let timestamp_us = (data.timestamp * 1_000_000.0) as i64;

    match data.content {
        CommonEncodedDataContent::FormatInfo(mut map) => {
            if track_index.is_some() {
                println!("track already has metadata");
                return Ok(());
            }

            // Override width and height with original values for video tracks
            if let (Some(width), Some(height)) = (original_width, original_height) {
                map.insert(
                    "width".to_string(),
                    crate::common::MediaFormatValue::Integer(width as i32),
                );
                map.insert(
                    "height".to_string(),
                    crate::common::MediaFormatValue::Integer(height as i32),
                );

                // The track format is what MediaMuxer writes the `colr` box from. These keys are
                // set on the encoder and most codecs echo them back in their output format, but
                // that is not guaranteed across vendors, so state them here as well. The values
                // must match the ones `create_video_format` configures the encoder with.
                map.insert(
                    format_keys::KEY_COLOR_STANDARD.to_string(),
                    crate::common::MediaFormatValue::Integer(COLOR_STANDARD_BT709),
                );
                map.insert(
                    format_keys::KEY_COLOR_TRANSFER.to_string(),
                    crate::common::MediaFormatValue::Integer(COLOR_TRANSFER_SDR_VIDEO),
                );
                map.insert(
                    format_keys::KEY_COLOR_RANGE.to_string(),
                    crate::common::MediaFormatValue::Integer(COLOR_RANGE_LIMITED),
                );
            }

            let mut shared_state_lock = shared_state.write().await;
            let shared_state = &mut *shared_state_lock;
            {
                let mut env = attach_current_thread()?;
                let format = crate::common::map_to_format(&mut env, &map)?;
                let format = SafeGlobalRef::new(&env, format)?;
                *track_index = Some(add_track(&mut env, muxer, &format)?);
            }
            match shared_state {
                MuxerSharedState::None => {
                    let (tx, rx) = oneshot::channel();
                    *shared_state = MuxerSharedState::Partial(tx);
                    drop(shared_state_lock);
                    rx.await??;
                }
                MuxerSharedState::Partial(_sender) => {
                    let mut env = attach_current_thread()?;
                    start_muxer(&mut env, muxer)?;
                    let prev = std::mem::replace(shared_state, MuxerSharedState::Started);
                    let MuxerSharedState::Partial(sender) = prev else {
                        panic!()
                    };
                    sender
                        .send(Ok(()))
                        .map_err(|_| AndroidError::ChannelSendFailed("start"))?;
                }
                MuxerSharedState::Started => {
                    return Err(AndroidError::MuxerAlreadyStarted);
                }
            };
        }
        CommonEncodedDataContent::Buffer { data, buffer_flag } => {
            let Some(track_index) = track_index else {
                return Err(AndroidError::MissingTrackMetadata);
            };
            let env = &mut attach_current_thread()?;
            let flags = buffer_flag;

            // println!("writing sample data: is_video: {}, flags({}): {:?}, length: {}, timestamp: {}", is_video, track_index, flags, data.len(), timestamp_us);

            write_sample_data(env, muxer, *track_index, &data, timestamp_us, flags)?;
        }
    }
    Ok(())
}
impl MuxerInput for MediaMuxerVideoInput {
    type Data = CommonEncodedData;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        push(
            data,
            self.shared_state.clone(),
            &self.muxer,
            &mut self.video_track_index,
            Some(self.original_width),
            Some(self.original_height),
        )
        .await
        .map_err(Into::into)
    }

    async fn finish(self) -> unienc_common::Result<()> {
        self.finish_tx.send(Ok(())).map_err(|_| {
            unienc_common::CommonError::from(AndroidError::ChannelSendFailed("finish"))
        })?;
        Ok(())
    }
}

impl MuxerInput for MediaMuxerAudioInput {
    type Data = CommonEncodedData;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        push(
            data,
            self.shared_state.clone(),
            &self.muxer,
            &mut self.audio_track_index,
            None, // No size override for audio
            None,
        )
        .await
        .map_err(Into::into)
    }

    async fn finish(self) -> unienc_common::Result<()> {
        self.finish_tx.send(Ok(())).map_err(|_| {
            unienc_common::CommonError::from(AndroidError::ChannelSendFailed("finish"))
        })?;
        Ok(())
    }
}

impl CompletionHandle for MediaMuxerCompletionHandle {
    async fn finish(self) -> unienc_common::Result<()> {
        finish_completion_handle_impl(self)
            .await
            .map_err(Into::into)
    }
}

async fn finish_completion_handle_impl(handle: MediaMuxerCompletionHandle) -> Result<()> {
    println!("waiting for all tracks to finish");

    handle.video_finish_rx.await??;
    handle.audio_finish_rx.await??;
    // Stop and release muxer
    let shared_state = handle.shared_state.read().await;
    let env = &mut attach_current_thread()?;
    if let MuxerSharedState::Started = *shared_state {
        stop_muxer(env, &handle.muxer)?;
    }

    release_muxer(env, &handle.muxer)?;

    Ok(())
}

// Helper functions for MediaMuxer

fn create_media_muxer(env: &mut JNIEnv, output_path: &Path) -> Result<SafeGlobalRef> {
    let path_str = output_path
        .to_str()
        .ok_or(AndroidError::InvalidOutputPath)?;
    let path_java = to_java_string(env, path_str)?;

    let muxer = bindings::MediaMuxer::new(env, &path_java, MUXER_OUTPUT_FORMAT_MPEG_4)?;

    SafeGlobalRef::new(env, muxer)
}

fn add_track(env: &mut JNIEnv, muxer: &SafeGlobalRef, format: &SafeGlobalRef) -> Result<jint> {
    Ok(bindings::MediaMuxer::add_track(
        env,
        muxer.as_obj(),
        format.as_obj(),
    )?)
}

fn start_muxer(env: &mut JNIEnv, muxer: &SafeGlobalRef) -> Result<()> {
    bindings::MediaMuxer::start(env, muxer.as_obj())?;
    Ok(())
}

fn stop_muxer(env: &mut JNIEnv, muxer: &SafeGlobalRef) -> Result<()> {
    bindings::MediaMuxer::stop(env, muxer.as_obj())?;
    Ok(())
}

fn release_muxer(env: &mut JNIEnv, muxer: &SafeGlobalRef) -> Result<()> {
    bindings::MediaMuxer::release(env, muxer.as_obj())?;
    Ok(())
}

fn write_sample_data(
    env: &mut JNIEnv,
    muxer: &SafeGlobalRef,
    track_index: jint,
    data: &[u8],
    timestamp: i64,
    flags: jint,
) -> Result<()> {
    // Create ByteBuffer
    let byte_buffer = unsafe { env.new_direct_byte_buffer(data.as_ptr() as *mut u8, data.len()) }?;

    // Create MediaCodec.BufferInfo
    let buffer_info = bindings::BufferInfo::new(env)?;

    // Set buffer info fields
    bindings::BufferInfo::set_offset(env, &buffer_info, 0)?;
    bindings::BufferInfo::set_size(env, &buffer_info, data.len() as jint)?;
    bindings::BufferInfo::set_presentation_time_us(env, &buffer_info, timestamp)?;
    bindings::BufferInfo::set_flags(env, &buffer_info, flags)?;

    // Write sample
    bindings::MediaMuxer::write_sample_data(
        env,
        muxer.as_obj(),
        track_index,
        &byte_buffer,
        &buffer_info,
    )?;
    Ok(())
}
