use std::ffi::{c_char, CStr};
use std::os::raw::c_void;
use std::path::Path;
use std::sync::Arc;
use tokio::sync::Mutex;
use unienc::{Encoder, EncodingSystem, Muxer, ResultExt};
use crate::*;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_new_encoding_system(
    runtime: *mut Runtime,
    video_options: *const VideoEncoderOptionsNative,
    audio_options: *const AudioEncoderOptionsNative,
) -> *mut PlatformEncodingSystem {
    let _guard = unsafe { &*runtime }.enter();
    unsafe {
        let system = PlatformEncodingSystem::new(&*video_options, &*audio_options);
        Box::into_raw(Box::new(system))
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_free_encoding_system(system: *mut PlatformEncodingSystem) {
    if !system.is_null() {
        unsafe {
            let _ = Box::from_raw(system);
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_new_video_encoder(
    runtime: *mut Runtime,
    system: *const PlatformEncodingSystem,
    input_out: *mut *const Mutex<Option<VideoEncoderInput>>,
    output_out: *mut *const Mutex<Option<VideoEncoderOutput>>,
    on_error: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) -> bool {
    let _guard = unsafe { &*runtime }.enter();
    let on_error: UniencCallback = unsafe { std::mem::transmute(on_error) };

    if system.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(on_error, user_data);
        return false;
    }

    unsafe {
        match (&*system).new_video_encoder() {
            Ok(encoder) => match encoder.get().context("Failed to get encoded video sample") {
                Ok((input, output)) => {
                    *input_out = Arc::into_raw(Arc::new(Mutex::new(Some(input))));
                    *output_out = Arc::into_raw(Arc::new(Mutex::new(Some(output))));
                    true
                }
                Err(err) => {
                    UniencError::from_common(err).apply_callback(on_error, user_data);
                    false
                }
            },
            Err(err) => {
                UniencError::from_common(err).apply_callback(on_error, user_data);
                false
            }
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_new_audio_encoder(
    runtime: *mut Runtime,
    system: *const PlatformEncodingSystem,
    input_out: *mut *const Mutex<Option<AudioEncoderInput>>,
    output_out: *mut *const Mutex<Option<AudioEncoderOutput>>,
    on_error: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) -> bool {
    let _guard = unsafe { &*runtime }.enter();
    let on_error: UniencCallback = unsafe { std::mem::transmute(on_error) };

    if system.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(on_error, user_data);
        return false;
    }

    unsafe {
        match (&*system).new_audio_encoder() {
            Ok(encoder) => match encoder.get().context("Failed to get encoded audio sample") {
                Ok((input, output)) => {
                    *input_out = Arc::into_raw(Arc::new(Mutex::new(Some(input))));
                    *output_out = Arc::into_raw(Arc::new(Mutex::new(Some(output))));
                    true
                }
                Err(err) => {
                    UniencError::from_common(err).apply_callback(on_error, user_data);
                    false
                }
            },
            Err(err) => {
                UniencError::from_common(err).apply_callback(on_error, user_data);
                false
            }
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_new_muxer(
    runtime: *mut Runtime,
    system: *const PlatformEncodingSystem,
    output_path: *const c_char,
    video_input_out: *mut *const Mutex<Option<VideoMuxerInput>>,
    audio_input_out: *mut *const Mutex<Option<AudioMuxerInput>>,
    completion_handle_out: *mut *const Mutex<Option<MuxerCompletionHandle>>,
    on_error: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) -> bool {
    let _guard = unsafe { &*runtime }.enter();
    let on_error: UniencCallback = unsafe { std::mem::transmute(on_error) };

    if system.is_null() || output_path.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(on_error, user_data);
        return false;
    }

    unsafe {
        let path_str = match CStr::from_ptr(output_path).to_str() {
            Ok(s) => s,
            Err(_) => {
                UniencError::invalid_input_error("Invalid input parameters")
                    .apply_callback(on_error, user_data);
                return false;
            }
        };
        let path = Path::new(path_str);

        match (&*system).new_muxer(path) {
            Ok(muxer) => {
                match muxer.get_inputs().context("Failed to get muxer input") {
                    Ok((video_input, audio_input, completion_handle)) => {
                        // Box the completion handle and store as raw pointer

                        *video_input_out = Arc::into_raw(Arc::new(Mutex::new(Some(video_input))));
                        *audio_input_out = Arc::into_raw(Arc::new(Mutex::new(Some(audio_input))));
                        *completion_handle_out =
                            Arc::into_raw(Arc::new(Mutex::new(Some(completion_handle))));
                        true
                    }
                    Err(err) => {
                        UniencError::from_common(err).apply_callback(on_error, user_data);
                        false
                    }
                }
            }
            Err(err) => {
                UniencError::from_common(err).apply_callback(on_error, user_data);
                false
            }
        }
    }
}