use std::ffi::c_void;

use anyhow::Context;
use tokio::sync::Mutex;
use unienc_common::{EncoderInput, EncoderOutput, VideoSample};

use crate::{
    arc_from_raw, arc_from_raw_retained,
    platform_types::{VideoEncoderInput, VideoEncoderOutput},
    ApplyCallback, Runtime, SendPtr, UniencCallback, UniencDataCallback, UniencError,
};

// Video encoder input/output functions
#[no_mangle]
pub unsafe extern "C" fn unienc_video_encoder_push(
    runtime: *mut Runtime,
    input: SendPtr<Mutex<Option<VideoEncoderInput>>>,
    data: SendPtr<u8>,
    data_size: usize,
    width: u32,
    height: u32,
    timestamp: f64,
    callback: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) {
    let _guard = (*runtime).enter();
    let callback: UniencCallback = std::mem::transmute(callback);
    if input.is_null() || data.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let input = arc_from_raw_retained(*input);

    unsafe {
        tokio::spawn(async move {
            let mut input = input.lock().await;
            let data_slice = std::slice::from_raw_parts(*data, data_size);
            let sample = VideoSample {
                data: data_slice.to_vec(),
                width,
                height,
                timestamp,
            };

            let result = match input
                .as_mut()
                .ok_or(UniencError::resource_allocation_error("Resource is None"))
            {
                Ok(input) => input
                    .push(&sample)
                    .await
                    .context("Failed to pull video sample")
                    .map_err(UniencError::from_anyhow),
                Err(err) => Err(err),
            };

            result.apply_callback(callback, user_data);
        });
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_video_encoder_pull(
    runtime: *mut Runtime,
    output: SendPtr<Mutex<Option<VideoEncoderOutput>>>,
    callback: usize, /*UniencDataCallback*/
    user_data: SendPtr<c_void>,
) {
    let _guard = (*runtime).enter();
    let callback: UniencDataCallback = std::mem::transmute(callback);
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
