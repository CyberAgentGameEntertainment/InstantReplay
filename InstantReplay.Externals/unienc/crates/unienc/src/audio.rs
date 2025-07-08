use std::ffi::c_void;

use tokio::sync::Mutex;
use unienc_common::{AudioSample, EncoderInput, EncoderOutput};

use crate::{
    arc_from_raw, arc_from_raw_retained,
    platform_types::{AudioEncoderInput, AudioEncoderOutput},
    ApplyCallback, SendPtr, UniencCallback, UniencDataCallback, UniencError, RUNTIME,
};

// Audio encoder input/output functions
#[no_mangle]
pub unsafe extern "C" fn unienc_audio_encoder_push(
    input: SendPtr<Mutex<Option<AudioEncoderInput>>>,
    data: SendPtr<i16>,
    sample_count: usize,
    timestamp_in_samples: u64,
    callback: UniencCallback,
    user_data: SendPtr<c_void>,
) {
    if input.is_null() || data.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }
    let input = arc_from_raw_retained(*input);

    unsafe {
        RUNTIME.with(|rt| {
            rt.spawn(async move {
                let data_slice = std::slice::from_raw_parts(*data, sample_count);
                let sample = AudioSample {
                    data: data_slice.to_vec(),
                    timestamp_in_samples,
                };
                let mut input = input.lock().await;
                let result = match input
                    .as_mut()
                    .ok_or(UniencError::resource_allocation_error("Resource is None"))
                {
                    Ok(input) => input.push(&sample).await.map_err(UniencError::from_anyhow),
                    Err(err) => Err(err),
                };
                result.apply_callback(callback, user_data);
            });
        });
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_audio_encoder_pull(
    output: SendPtr<Mutex<Option<AudioEncoderOutput>>>,
    callback: UniencDataCallback,
    user_data: SendPtr<c_void>,
) {
    if output.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }
    let output = arc_from_raw_retained(*output);

    RUNTIME.with(|rt| {
        rt.spawn(async move {
            let mut output = output.lock().await;
            let result = match output
                .as_mut()
                .ok_or(UniencError::resource_allocation_error("Resource is None"))
            {
                Ok(output) => output.pull().await.map_err(UniencError::from_anyhow),
                Err(err) => Err(err),
            };
            result.apply_callback(callback, user_data);
        });
    });
}

#[no_mangle]
pub unsafe extern "C" fn unienc_free_audio_encoder_input(
    audio_input: SendPtr<Mutex<Option<AudioEncoderInput>>>,
) {
    if !audio_input.is_null() {
        arc_from_raw(*audio_input);
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_free_audio_encoder_output(
    audio_output: SendPtr<Mutex<Option<AudioEncoderOutput>>>,
) {
    if !audio_output.is_null() {
        arc_from_raw(*audio_output);
    }
}
