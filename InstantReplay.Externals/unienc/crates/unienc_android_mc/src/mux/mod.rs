use anyhow::Result;
use jni::{objects::JValue, sys::jint, JNIEnv};
use std::{path::Path, sync::Arc};
use tokio::sync::{oneshot, RwLock};
use unienc_common::{CompletionHandle, Muxer, MuxerInput};

use crate::{common::*, config::muxer_formats::*};

use crate::java::*;

pub struct MediaMuxer {
    video_input: MediaMuxerVideoInput,
    audio_input: MediaMuxerAudioInput,
    completion_handle: MediaMuxerCompletionHandle,
}

enum MuxerSharedState {
    None,
    Partial(oneshot::Sender<Result<()>>),
    Started,
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
    ) -> Result<(
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
            }
            println!("acquiring shared state lock");

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
                    println!("waiting until other side starts");
                    rx.await??;
                }
                MuxerSharedState::Partial(_sender) => {
                    let mut env = attach_current_thread()?;
                    println!("starting muxer");
                    start_muxer(&mut env, muxer)?;
                    let prev = std::mem::replace(shared_state, MuxerSharedState::Started);
                    let MuxerSharedState::Partial(sender) = prev else {
                        panic!()
                    };
                    sender
                        .send(Ok(()))
                        .map_err(|_| anyhow::anyhow!("failed to send start signal"))?;
                }
                MuxerSharedState::Started => {
                    return Err(anyhow::anyhow!("muxer already started"));
                }
            };
        }
        CommonEncodedDataContent::Buffer { data, buffer_flag } => {
            let Some(track_index) = track_index else {
                return Err(anyhow::anyhow!("track does not have metadata"));
            };
            let env = &mut attach_current_thread()?;
            let flags = buffer_flag;

            // println!("writing sample data: is_video: {}, flags({}): {:?}, length: {}, timestamp: {}", is_video, track_index, flags, data.len(), timestamp_us);

            write_sample_data(env, muxer, *track_index, &data, timestamp_us, flags)?;
        },
    }
    Ok(())
}
impl MuxerInput for MediaMuxerVideoInput {
    type Data = CommonEncodedData;

    async fn push(&mut self, data: Self::Data) -> Result<()> {
        push(
            data,
            self.shared_state.clone(),
            &self.muxer,
            &mut self.video_track_index,
            Some(self.original_width),
            Some(self.original_height),
        )
        .await
    }

    async fn finish(self) -> Result<()> {
        self.finish_tx
            .send(Ok(()))
            .map_err(|_| anyhow::anyhow!("failed to send finish signal"))?;
        Ok(())
    }
}

impl MuxerInput for MediaMuxerAudioInput {
    type Data = CommonEncodedData;

    async fn push(&mut self, data: Self::Data) -> Result<()> {
        push(
            data,
            self.shared_state.clone(),
            &self.muxer,
            &mut self.audio_track_index,
            None, // No size override for audio
            None,
        )
        .await
    }

    async fn finish(self) -> Result<()> {
        self.finish_tx
            .send(Ok(()))
            .map_err(|_| anyhow::anyhow!("failed to send finish signal"))?;
        Ok(())
    }
}

impl CompletionHandle for MediaMuxerCompletionHandle {
    async fn finish(self) -> Result<()> {
        println!("waiting for all tracks to finish");

        self.video_finish_rx.await??;
        self.audio_finish_rx.await??;
        // Stop and release muxer
        let shared_state = self.shared_state.read().await;
        let env = &mut attach_current_thread()?;
        if let MuxerSharedState::Started = *shared_state {
            stop_muxer(env, &self.muxer)?;
        }

        release_muxer(env, &self.muxer)?;

        Ok(())
    }
}

// Helper functions for MediaMuxer

fn create_media_muxer(env: &mut JNIEnv, output_path: &Path) -> Result<SafeGlobalRef> {
    let muxer_class = env.find_class("android/media/MediaMuxer")?;

    let path_str = output_path
        .to_str()
        .ok_or_else(|| anyhow::anyhow!("Invalid output path"))?;
    let path_java = to_java_string(env, path_str)?;

    let muxer = env.new_object(
        muxer_class,
        "(Ljava/lang/String;I)V",
        &[
            JValue::Object(&path_java),
            JValue::Int(OUTPUT_FORMAT_MPEG_4),
        ],
    )?;

    SafeGlobalRef::new(env, muxer)
}

fn add_track(env: &mut JNIEnv, muxer: &SafeGlobalRef, format: &SafeGlobalRef) -> Result<jint> {
    call_int_method(
        env,
        muxer.as_obj(),
        "addTrack",
        "(Landroid/media/MediaFormat;)I",
        &[JValue::Object(format.as_obj())],
    )
}

fn start_muxer(env: &mut JNIEnv, muxer: &SafeGlobalRef) -> Result<()> {
    call_void_method(env, muxer.as_obj(), "start", "()V", &[])
}

fn stop_muxer(env: &mut JNIEnv, muxer: &SafeGlobalRef) -> Result<()> {
    call_void_method(env, muxer.as_obj(), "stop", "()V", &[])
}

fn release_muxer(env: &mut JNIEnv, muxer: &SafeGlobalRef) -> Result<()> {
    call_void_method(env, muxer.as_obj(), "release", "()V", &[])
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
    let buffer_info_class = env.find_class("android/media/MediaCodec$BufferInfo")?;
    let buffer_info = env.new_object(buffer_info_class, "()V", &[])?;

    // Set buffer info fields
    env.set_field(&buffer_info, "offset", "I", JValue::Int(0 as jint))?;
    env.set_field(&buffer_info, "size", "I", JValue::Int(data.len() as jint))?;
    env.set_field(
        &buffer_info,
        "presentationTimeUs",
        "J",
        JValue::Long(timestamp),
    )?;
    env.set_field(&buffer_info, "flags", "I", JValue::Int(flags))?;

    // Write sample
    call_void_method(
        env,
        muxer.as_obj(),
        "writeSampleData",
        "(ILjava/nio/ByteBuffer;Landroid/media/MediaCodec$BufferInfo;)V",
        &[
            JValue::Int(track_index),
            JValue::Object(&byte_buffer),
            JValue::Object(&buffer_info),
        ],
    )
}
