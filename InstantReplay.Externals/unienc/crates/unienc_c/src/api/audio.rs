use std::ffi::c_void;

use tokio::sync::Mutex;
use unienc::{AudioSample, EncoderInput, EncoderOutput, ResultExt};
use crate::*;

// Audio encoder input/output functions
#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_audio_encoder_push(
    runtime: *mut Runtime,
    input: SendPtr<Mutex<Option<AudioEncoderInput>>>,
    data: SendPtr<i16>,
    sample_count: usize,
    timestamp_in_samples: u64,
    callback: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) {
    let callback: UniencCallback = unsafe { std::mem::transmute(callback) };
    let Some(runtime) = (unsafe { runtime.as_ref() }) else  {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    };
    if input.is_null() || data.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }
    let _guard = runtime.enter();
    let input = arc_from_raw_retained(*input);

    unsafe {
        Runtime::spawn(async move {
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
                Ok(input) => input
                    .push(sample)
                    .await
                    .context("Failed to push audio sample")
                    .map_err(UniencError::from_common),
                Err(err) => Err(err),
            };
            result.apply_callback(callback, user_data);
        });
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_audio_encoder_pull(
    runtime: *mut Runtime,
    output: SendPtr<Mutex<Option<AudioEncoderOutput>>>,
    callback: usize, /*UniencDataCallback<UniencSampleData>*/
    user_data: SendPtr<c_void>,
) {
    let callback: UniencDataCallback<UniencSampleData> = unsafe { std::mem::transmute(callback) };
    let Some(runtime) = (unsafe { runtime.as_ref() }) else  {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    };
    if output.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return;
    }
    let _guard = runtime.enter();
    let output = arc_from_raw_retained(*output);

    Runtime::spawn(async move {
        let mut output = output.lock().await;
        let result = match output
            .as_mut()
            .ok_or(UniencError::resource_allocation_error("Resource is None"))
        {
            Ok(output) => output
                .pull()
                .await
                .context("Failed to pull audio sample")
                .map_err(UniencError::from_common),
            Err(err) => Err(err),
        };
        result.apply_callback(callback, user_data);
    });
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_free_audio_encoder_input(
    runtime: *mut Runtime,
    audio_input: SendPtr<Mutex<Option<AudioEncoderInput>>>,
) {
    let _guard = unsafe { runtime.as_ref() }.unwrap().enter();
    if !audio_input.is_null() {
        arc_from_raw(*audio_input);
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_free_audio_encoder_output(
    runtime: *mut Runtime,
    audio_output: SendPtr<Mutex<Option<AudioEncoderOutput>>>,
) {
    let _guard = unsafe { runtime.as_ref() }.unwrap().enter();
    if !audio_output.is_null() {
        arc_from_raw(*audio_output);
    }
}
