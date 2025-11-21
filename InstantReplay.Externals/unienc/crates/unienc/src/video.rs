use std::ffi::c_void;

use anyhow::Context;
use tokio::sync::Mutex;
use unienc_common::{
    buffer::SharedBuffer, EncoderInput, EncoderOutput, TryFromRaw, VideoFrame, VideoFrameBgra32,
    VideoSample,
};

use crate::{
    arc_from_raw, arc_from_raw_retained,
    blit::UniencBlitTargetData,
    platform_types::{BlitTarget, VideoEncoderInput, VideoEncoderOutput},
    ApplyCallback, Runtime, SendPtr, UniencCallback, UniencDataCallback, UniencError,
    UniencSampleData,
};

// Video encoder input/output functions
#[no_mangle]
pub unsafe extern "C" fn unienc_video_encoder_push_shared_buffer(
    runtime: *mut Runtime,
    input: SendPtr<Mutex<Option<VideoEncoderInput>>>,
    buffer: SendPtr<SharedBuffer>,
    width: u32,
    height: u32,
    timestamp: f64,
    callback: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) {
    let callback: UniencCallback = std::mem::transmute(callback);
    if input.is_null() || buffer.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }
    let buffer = Box::from_raw(*buffer);
    let sample = VideoSample {
        frame: VideoFrame::Bgra32(VideoFrameBgra32 {
            buffer: *buffer,
            width,
            height,
        }),
        timestamp,
    };

    video_encoder_push_video_sample(runtime, input, sample, callback, user_data);
}

#[no_mangle]
pub unsafe extern "C" fn unienc_video_encoder_push_blit_target(
    runtime: *mut Runtime,
    input: SendPtr<Mutex<Option<VideoEncoderInput>>>,
    blit_target: UniencBlitTargetData,
    timestamp: f64,
    callback: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) {
    let callback: UniencCallback = std::mem::transmute(callback);
    if input.is_null() || blit_target.data.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let blit_target = match <BlitTarget as TryFromRaw>::try_from_raw(blit_target.data) {
        Ok(blit_target) => blit_target,
        Err(_) => {
            UniencError::invalid_input_error("Failed to convert blit target data")
                .apply_callback(callback, user_data);
            return;
        }
    };

    let sample = VideoSample {
        frame: VideoFrame::BlitTarget(blit_target),
        timestamp,
    };

    video_encoder_push_video_sample(runtime, input, sample, callback, user_data);
}

unsafe fn video_encoder_push_video_sample(
    runtime: *mut Runtime,
    input: SendPtr<Mutex<Option<VideoEncoderInput>>>,
    sample: VideoSample<BlitTarget>,
    callback: UniencCallback,
    user_data: SendPtr<c_void>,
) {
    let _guard = (*runtime).enter();
    let callback: UniencCallback = std::mem::transmute(callback);

    let input = arc_from_raw_retained(*input);

    tokio::spawn(async move {
        let mut input = input.lock().await;

        let result = match input
            .as_mut()
            .ok_or(UniencError::resource_allocation_error("Resource is None"))
        {
            Ok(input) => input
                .push(sample)
                .await
                .context("Failed to push video sample")
                .map_err(UniencError::from_anyhow),
            Err(err) => Err(err),
        };

        result.apply_callback(callback, user_data);
    });
}

#[no_mangle]
pub unsafe extern "C" fn unienc_video_encoder_pull(
    runtime: *mut Runtime,
    output: SendPtr<Mutex<Option<VideoEncoderOutput>>>,
    callback: usize, /*UniencDataCallback<UniencSampleData>*/
    user_data: SendPtr<c_void>,
) {
    let _guard = (*runtime).enter();
    let callback: UniencDataCallback<UniencSampleData> = std::mem::transmute(callback);
    if output.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let output = arc_from_raw_retained(*output);

    tokio::spawn(async move {
        let mut output = output.lock().await;
        let result = match output
            .as_mut()
            .ok_or(UniencError::resource_allocation_error("Resource is None"))
        {
            Ok(output) => {
                let result = output
                    .pull()
                    .await
                    .context("Failed to pull video sample")
                    .map_err(UniencError::from_anyhow);
                result
            }
            Err(err) => Err(err),
        };
        result.apply_callback(callback, user_data);
    });
}

#[no_mangle]
pub unsafe extern "C" fn unienc_free_video_encoder_input(
    video_input: SendPtr<Mutex<Option<VideoEncoderInput>>>,
) {
    if !video_input.is_null() {
        arc_from_raw(*video_input);
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_free_video_encoder_output(
    video_output: SendPtr<Mutex<Option<VideoEncoderOutput>>>,
) {
    if !video_output.is_null() {
        arc_from_raw(*video_output);
    }
}
