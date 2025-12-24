use std::ffi::c_void;

use crate::*;
use tokio::sync::Mutex;
use unienc::{
    buffer::SharedBuffer, EncoderInput, EncoderOutput, ResultExt, TryFromUnityNativeTexturePointer,
    VideoFrame, VideoFrameBgra32, VideoSample,
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
pub unsafe extern "C" fn unienc_video_encoder_push_blit_source(
    runtime: *mut Runtime,
    input: SendPtr<Mutex<Option<VideoEncoderInput>>>,
    source_native_texture_ptr: *mut c_void,
    width: u32,
    height: u32,
    graphics_format: u32,
    flip_vertically: bool,
    is_gamma_workflow: bool,
    timestamp: f64,
    issue_graphics_event_callback: usize, /* UniencIssueGraphicsEventCallback */
    callback: usize,                      /*UniencCallback*/
    user_data: SendPtr<c_void>,
) {
    let callback: UniencCallback = std::mem::transmute(callback);
    if input.is_null() || source_native_texture_ptr.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }
    let unienc_issue_graphics_event_callback: UniencIssueGraphicsEventCallback =
        std::mem::transmute(issue_graphics_event_callback);

    // weak runtime for graphics event
    let Some(weak) = runtime.as_ref().map(|r| r.weak()) else {
        UniencError::invalid_input_error("Invalid runtime pointer")
            .apply_callback(callback, user_data);
        return;
    };

    match BlitSource::try_from_unity_native_texture_ptr(source_native_texture_ptr) {
        Ok(blit_source) => {
            let sample = VideoSample {
                frame: VideoFrame::BlitSource {
                    source: blit_source,
                    width,
                    height,
                    graphics_format,
                    flip_vertically,
                    is_gamma_workflow,
                    event_issuer: Box::new(UniencGraphicsEventIssuer::new(
                        unienc_issue_graphics_event_callback,
                        weak
                    )),
                },
                timestamp,
            };
            video_encoder_push_video_sample(runtime, input, sample, callback, user_data);
        }
        Err(err) => {
            UniencError::from_common(err).apply_callback(callback, user_data);
        }
    }
}

unsafe fn video_encoder_push_video_sample(
    runtime: *mut Runtime,
    input: SendPtr<Mutex<Option<VideoEncoderInput>>>,
    sample: VideoSample<BlitSource>,
    callback: UniencCallback,
    user_data: SendPtr<c_void>,
) {
    let _guard = (*runtime).enter();

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
                .map_err(UniencError::from_common),
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

    spawn_optimistically(async move {
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
                    .map_err(UniencError::from_common);
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
