use std::ffi::c_void;

use tokio::sync::Mutex;
use unienc_common::{CompletionHandle, EncodedData, MuxerInput};

use crate::{
    arc_from_raw, arc_from_raw_retained,
    platform_types::{
        AudioEncodedData, AudioMuxerInput, MuxerCompletionHandle, VideoEncodedData, VideoMuxerInput,
    },
    ApplyCallback, Runtime, SendPtr, UniencCallback, UniencError,
};

// Muxer input functions
#[no_mangle]
pub unsafe extern "C" fn unienc_muxer_push_video(
    runtime: *mut Runtime,
    video_input: SendPtr<Mutex<Option<VideoMuxerInput>>>,
    data: SendPtr<u8>,
    size: usize,
    timestamp: f64,
    callback: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) {
    let _guard = (*runtime).enter();
    let callback: UniencCallback = std::mem::transmute(callback);
    if video_input.is_null() || data.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let video_input = arc_from_raw_retained(*video_input);

    unsafe {
        let data_slice = std::slice::from_raw_parts(*data, size);

        // Deserialize the encoded data
        let mut decoded_data: VideoEncodedData =
            match bincode::decode_from_slice::<_, _>(data_slice, bincode::config::standard()) {
                Ok((data, _)) => data,
                Err(_) => {
                    UniencError::encoding_error("Failed to decode encoded data")
                        .apply_callback(callback, user_data);
                    return;
                }
            };

        decoded_data.set_timestamp(timestamp);

        tokio::spawn(async move {
            let mut video_input = video_input.lock().await;
            let result = match video_input
                .as_mut()
                .ok_or(UniencError::resource_allocation_error("Resource is None"))
            {
                Ok(video_input) => video_input
                    .push(decoded_data)
                    .await
                    .map_err(UniencError::from_anyhow),
                Err(err) => Err(err),
            };
            result.apply_callback(callback, user_data);
        });
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_muxer_push_audio(
    runtime: *mut Runtime,
    audio_input: SendPtr<Mutex<Option<AudioMuxerInput>>>,
    data: SendPtr<u8>,
    size: usize,
    timestamp: f64,
    callback: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) {
    let _guard = (*runtime).enter();
    let callback: UniencCallback = std::mem::transmute(callback);
    if audio_input.is_null() || data.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let audio_input = arc_from_raw_retained(*audio_input);

    unsafe {
        let data_slice = std::slice::from_raw_parts(*data, size);

        // Deserialize the encoded data
        let mut decoded_data: AudioEncodedData =
            match bincode::decode_from_slice::<_, _>(data_slice, bincode::config::standard()) {
                Ok((data, _)) => data,
                Err(_) => {
                    UniencError::encoding_error("Failed to decode encoded data")
                        .apply_callback(callback, user_data);
                    return;
                }
            };

        decoded_data.set_timestamp(timestamp);

        tokio::spawn(async move {
            let mut audio_input = audio_input.lock().await;
            let result = match audio_input
                .as_mut()
                .ok_or(UniencError::resource_allocation_error("Resource is None"))
            {
                Ok(audio_input) => audio_input
                    .push(decoded_data)
                    .await
                    .map_err(UniencError::from_anyhow),
                Err(err) => Err(err),
            };
            result.apply_callback(callback, user_data);
        });
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_muxer_finish_video(
    runtime: *mut Runtime,
    video_input: SendPtr<Mutex<Option<VideoMuxerInput>>>,
    callback: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) {
    let _guard = (*runtime).enter();
    let callback: UniencCallback = std::mem::transmute(callback);
    if video_input.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let video_input = arc_from_raw_retained(*video_input);

    tokio::spawn(async move {
        let mut video_input = video_input.lock().await;
        let result = match video_input
            .take()
            .ok_or(UniencError::resource_allocation_error("Resource is None"))
        {
            Ok(video_input) => video_input.finish().await.map_err(UniencError::from_anyhow),
            Err(err) => Err(err),
        };
        result.apply_callback(callback, user_data);
    });
}

#[no_mangle]
pub unsafe extern "C" fn unienc_muxer_finish_audio(
    runtime: *mut Runtime,
    audio_input: SendPtr<Mutex<Option<AudioMuxerInput>>>,
    callback: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) {
    let _guard = (*runtime).enter();
    let callback: UniencCallback = std::mem::transmute(callback);
    if audio_input.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let audio_input = arc_from_raw_retained(*audio_input);

    tokio::spawn(async move {
        let mut audio_input = audio_input.lock().await;
        let result = match audio_input
            .take()
            .ok_or(UniencError::resource_allocation_error("Resource is None"))
        {
            Ok(audio_input) => audio_input.finish().await.map_err(UniencError::from_anyhow),
            Err(err) => Err(err),
        };
        result.apply_callback(callback, user_data);
    });
}

#[no_mangle]
pub unsafe extern "C" fn unienc_muxer_complete(
    runtime: *mut Runtime,
    completion_handle: SendPtr<Mutex<Option<MuxerCompletionHandle>>>,
    callback: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) {
    let _guard = (*runtime).enter();
    let callback: UniencCallback = std::mem::transmute(callback);
    if completion_handle.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let handle = arc_from_raw_retained(*completion_handle);

    tokio::spawn(async move {
        let mut handle = handle.lock().await;

        let result = match handle
            .take()
            .ok_or(UniencError::resource_allocation_error("Resource is None"))
        {
            Ok(handle) => handle.finish().await.map_err(UniencError::from_anyhow),
            Err(err) => Err(err),
        };
        result.apply_callback(callback, user_data);
    });
}

// Free functions for muxer components
#[no_mangle]
pub unsafe extern "C" fn unienc_free_muxer_video_input(
    video_input: SendPtr<Mutex<Option<VideoMuxerInput>>>,
) {
    if !video_input.is_null() {
        arc_from_raw(*video_input);
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_free_muxer_audio_input(
    audio_input: SendPtr<Mutex<Option<AudioMuxerInput>>>,
) {
    if !audio_input.is_null() {
        arc_from_raw(*audio_input);
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_free_muxer_completion_handle(
    completion_handle: SendPtr<Mutex<Option<MuxerCompletionHandle>>>,
) {
    if !completion_handle.is_null() {
        arc_from_raw(*completion_handle);
    }
}
