use std::ffi::c_void;

use tokio::sync::Mutex;
use unienc_common::{CompletionHandle, MuxerInput};

use crate::{
    arc_from_raw, arc_from_raw_retained,
    platform_types::{AudioMuxerInput, MuxerCompletionHandle, VideoMuxerInput},
    ApplyCallback, SendPtr, UniencCallback, UniencError, RUNTIME,
};

// Muxer input functions
#[no_mangle]
pub unsafe extern "C" fn unienc_muxer_push_video(
    video_input: SendPtr<Mutex<Option<VideoMuxerInput>>>,
    data: SendPtr<u8>,
    size: usize,
    callback: UniencCallback,
    user_data: SendPtr<c_void>,
) {
    if video_input.is_null() || data.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    unsafe {
        let video_input = arc_from_raw_retained(*video_input);
        let data_slice = std::slice::from_raw_parts(*data, size);

        // Deserialize the encoded data
        let decoded_data =
            match bincode::decode_from_slice::<_, _>(data_slice, bincode::config::standard()) {
                Ok((data, _)) => data,
                Err(_) => {
                    UniencError::encoding_error("Failed to decode encoded data")
                        .apply_callback(callback, user_data);
                    return;
                }
            };

        RUNTIME.spawn(async move {
            let mut video_input = video_input.lock().await;
            let result = match video_input
                .as_mut()
                .ok_or(UniencError::resource_allocation_error("Resource is None"))
            {
                Ok(video_input) => video_input
                    .push(decoded_data)
                    .await
                    .map_err(|_e| UniencError::ERROR),
                Err(err) => Err(err),
            };
            result.apply_callback(callback, user_data);
        });
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_muxer_push_audio(
    audio_input: SendPtr<Mutex<Option<AudioMuxerInput>>>,
    data: SendPtr<u8>,
    size: usize,
    callback: UniencCallback,
    user_data: SendPtr<c_void>,
) {
    if audio_input.is_null() || data.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    unsafe {
        let audio_input = arc_from_raw_retained(*audio_input);
        let data_slice = std::slice::from_raw_parts(*data, size);

        // Deserialize the encoded data
        let decoded_data =
            match bincode::decode_from_slice::<_, _>(data_slice, bincode::config::standard()) {
                Ok((data, _)) => data,
                Err(_) => {
                    UniencError::encoding_error("Failed to decode encoded data")
                        .apply_callback(callback, user_data);
                    return;
                }
            };

        RUNTIME.spawn(async move {
            let mut audio_input = audio_input.lock().await;
            let result = match audio_input
                .as_mut()
                .ok_or(UniencError::resource_allocation_error("Resource is None"))
            {
                Ok(audio_input) => audio_input
                    .push(decoded_data)
                    .await
                    .map_err(|_e| UniencError::ERROR),
                Err(err) => Err(err),
            };
            result.apply_callback(callback, user_data);
        });
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_muxer_finish_video(
    video_input: SendPtr<Mutex<Option<VideoMuxerInput>>>,
    callback: UniencCallback,
    user_data: SendPtr<c_void>,
) {
    if video_input.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let video_input = arc_from_raw_retained(*video_input);

    RUNTIME.spawn(async move {
        let mut video_input = video_input.lock().await;
        let result = match video_input
            .take()
            .ok_or(UniencError::resource_allocation_error("Resource is None"))
        {
            Ok(video_input) => video_input
                .finish()
                .await
                .map_err(UniencError::from_anyhow),
            Err(err) => Err(err),
        };
        result.apply_callback(callback, user_data);
    });
}

#[no_mangle]
pub unsafe extern "C" fn unienc_muxer_finish_audio(
    audio_input: SendPtr<Mutex<Option<AudioMuxerInput>>>,
    callback: UniencCallback,
    user_data: SendPtr<c_void>,
) {
    if audio_input.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let audio_input = arc_from_raw_retained(*audio_input);

    RUNTIME.spawn(async move {
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
    completion_handle: SendPtr<Mutex<Option<MuxerCompletionHandle>>>,
    callback: UniencCallback,
    user_data: SendPtr<c_void>,
) {
    if completion_handle.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }

    let handle = arc_from_raw_retained(*completion_handle);

    RUNTIME.spawn(async move {
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
