use std::ffi::{c_char, c_void, CStr, CString};
use std::ops::Deref;
use std::path::Path;
use std::sync::{Arc, LazyLock};

use tokio::sync::Mutex;
use unienc_common::{EncodedData, Encoder, EncodingSystem, Muxer};

mod audio;
mod mux;
mod public_types;
mod video;

pub use public_types::*;

#[derive(Clone, Debug, PartialEq)]
pub struct UniencError {
    pub kind: UniencErrorKind,
    pub message: Option<String>,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct UniencErrorNative {
    pub kind: UniencErrorKind,
    pub message: *const c_char,
}

impl UniencErrorNative {
    pub const SUCCESS: Self = Self {
        kind: UniencErrorKind::Success,
        message: std::ptr::null(),
    };
}

impl UniencError {
    pub const SUCCESS: Self = Self {
        kind: UniencErrorKind::Success,
        message: None,
    };
    pub const ERROR: Self = Self {
        kind: UniencErrorKind::Error,
        message: None,
    };
    pub fn with_native(&self, f: impl FnOnce(&UniencErrorNative)) {
        let message = self
            .message
            .as_ref()
            .map(|string| CString::new(string.as_str()).unwrap());
        f(&UniencErrorNative {
            kind: self.kind,
            message: match message.as_ref() {
                Some(string) => string.as_ptr(),
                None => std::ptr::null(),
            },
        });
        drop(message);
    }

    /// Convert an anyhow::Error to UniencError with appropriate categorization
    pub fn from_anyhow(err: anyhow::Error) -> Self {
        let message = err.to_string();
        let kind = Self::categorize_error(&message);
        Self {
            kind,
            message: Some(message),
        }
    }

    /// Categorize error based on error message content
    fn categorize_error(message: &str) -> UniencErrorKind {
        // Convert to lowercase for case-insensitive matching
        let lower_message = message.to_lowercase();

        if lower_message.contains("failed to create")
            || lower_message.contains("failed to initialize")
        {
            UniencErrorKind::InitializationError
        } else if lower_message.contains("failed to configure")
            || lower_message.contains("configuration")
        {
            UniencErrorKind::ConfigurationError
        } else if lower_message.contains("null")
            || lower_message.contains("buffer too small")
            || lower_message.contains("no input buffer")
            || lower_message.contains("memory")
        {
            UniencErrorKind::ResourceAllocationError
        } else if lower_message.contains("encoding") || lower_message.contains("encode") {
            UniencErrorKind::EncodingError
        } else if lower_message.contains("mux") || lower_message.contains("writing") {
            UniencErrorKind::MuxingError
        } else if lower_message.contains("failed to send")
            || lower_message.contains("channel")
            || lower_message.contains("communication")
        {
            UniencErrorKind::CommunicationError
        } else if lower_message.contains("timeout") {
            UniencErrorKind::TimeoutError
        } else if lower_message.contains("invalid") || lower_message.contains("unsupported") {
            UniencErrorKind::InvalidInput
        } else if lower_message.contains("osstatus") || lower_message.contains("media") {
            UniencErrorKind::PlatformError
        } else {
            UniencErrorKind::Error // Default fallback
        }
    }

    // Specific error constructors for each error category
    pub fn initialization_error(msg: impl Into<String>) -> Self {
        Self {
            kind: UniencErrorKind::InitializationError,
            message: Some(msg.into()),
        }
    }

    pub fn configuration_error(msg: impl Into<String>) -> Self {
        Self {
            kind: UniencErrorKind::ConfigurationError,
            message: Some(msg.into()),
        }
    }

    pub fn resource_allocation_error(msg: impl Into<String>) -> Self {
        Self {
            kind: UniencErrorKind::ResourceAllocationError,
            message: Some(msg.into()),
        }
    }

    pub fn encoding_error(msg: impl Into<String>) -> Self {
        Self {
            kind: UniencErrorKind::EncodingError,
            message: Some(msg.into()),
        }
    }

    pub fn muxing_error(msg: impl Into<String>) -> Self {
        Self {
            kind: UniencErrorKind::MuxingError,
            message: Some(msg.into()),
        }
    }

    pub fn communication_error(msg: impl Into<String>) -> Self {
        Self {
            kind: UniencErrorKind::CommunicationError,
            message: Some(msg.into()),
        }
    }

    pub fn timeout_error(msg: impl Into<String>) -> Self {
        Self {
            kind: UniencErrorKind::TimeoutError,
            message: Some(msg.into()),
        }
    }

    pub fn invalid_input_error(msg: impl Into<String>) -> Self {
        Self {
            kind: UniencErrorKind::InvalidInput,
            message: Some(msg.into()),
        }
    }

    pub fn platform_error(msg: impl Into<String>) -> Self {
        Self {
            kind: UniencErrorKind::PlatformError,
            message: Some(msg.into()),
        }
    }
}

// Callback types for async operations
pub type UniencCallback = unsafe extern "C" fn(user_data: *mut c_void, error: UniencErrorNative);
pub type UniencDataCallback = unsafe extern "C" fn(
    user_data: *mut c_void,
    data: *const u8,
    size: usize,
    timestamp: f64,
    is_key: bool,
    error: UniencErrorNative,
);

// Send-safe wrappers for raw pointers
#[repr(transparent)]
pub struct SendPtr<T>(*mut T);
unsafe impl<T> Send for SendPtr<T> {}

impl<T> From<*mut T> for SendPtr<T> {
    fn from(ptr: *mut T) -> Self {
        SendPtr(ptr)
    }
}

impl<T> Deref for SendPtr<T> {
    type Target = *mut T;
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}

impl<T> From<SendPtr<T>> for *mut T {
    fn from(val: SendPtr<T>) -> Self {
        val.0
    }
}

pub fn add(left: u64, right: u64) -> u64 {
    left + right
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct VideoEncoderOptionsNative {
    pub width: u32,
    pub height: u32,
    pub fps_hint: u32,
    pub bitrate: u32,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct AudioEncoderOptionsNative {
    pub sample_rate: u32,
    pub channels: u32,
    pub bitrate: u32,
}

impl unienc_common::VideoEncoderOptions for VideoEncoderOptionsNative {
    fn width(&self) -> u32 {
        self.width
    }

    fn height(&self) -> u32 {
        self.height
    }

    fn fps_hint(&self) -> u32 {
        self.fps_hint
    }

    fn bitrate(&self) -> u32 {
        self.bitrate
    }
}

impl unienc_common::AudioEncoderOptions for AudioEncoderOptionsNative {
    fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    fn channels(&self) -> u32 {
        self.channels
    }

    fn bitrate(&self) -> u32 {
        self.bitrate
    }
}

#[cfg(target_vendor = "apple")]
type PlatformEncodingSystem = unienc_apple_vt::VideoToolboxEncodingSystem<
    VideoEncoderOptionsNative,
    AudioEncoderOptionsNative,
>;

#[cfg(target_os = "android")]
type PlatformEncodingSystem = unienc_android_mc::MediaCodecEncodingSystem<
    VideoEncoderOptionsNative,
    AudioEncoderOptionsNative,
>;

#[cfg(not(any(target_vendor = "apple", target_os = "android")))]
type PlatformEncodingSystem = (); // Placeholder - will generate compile error below

#[cfg(not(any(target_vendor = "apple", target_os = "android")))]
compile_error!(
    "Platform not supported. Only Apple and Android platforms are currently implemented."
);

mod platform_types {
    use unienc_common::EncoderOutput;

    type VideoEncoder =
        <crate::PlatformEncodingSystem as unienc_common::EncodingSystem>::VideoEncoderType;
    pub type VideoEncoderInput = <VideoEncoder as unienc_common::Encoder>::InputType;
    pub type VideoEncoderOutput = <VideoEncoder as unienc_common::Encoder>::OutputType;
    type AudioEncoder =
        <crate::PlatformEncodingSystem as unienc_common::EncodingSystem>::AudioEncoderType;
    pub type AudioEncoderInput = <AudioEncoder as unienc_common::Encoder>::InputType;
    pub type AudioEncoderOutput = <AudioEncoder as unienc_common::Encoder>::OutputType;
    type Muxer = <crate::PlatformEncodingSystem as unienc_common::EncodingSystem>::MuxerType;
    pub type VideoMuxerInput = <Muxer as unienc_common::Muxer>::VideoInputType;
    pub type AudioMuxerInput = <Muxer as unienc_common::Muxer>::AudioInputType;
    pub type MuxerCompletionHandle = <Muxer as unienc_common::Muxer>::CompletionHandleType;

    pub type VideoEncodedData = <VideoEncoderOutput as EncoderOutput>::Data;
    pub type AudioEncodedData = <AudioEncoderOutput as EncoderOutput>::Data;
}

use platform_types::*;

pub use unienc_common::{AudioEncoderOptions, VideoEncoderOptions};

// Runtime for async operations
use tokio::runtime::Runtime;

static RUNTIME: LazyLock<Runtime> = LazyLock::new(|| Runtime::new().unwrap());

#[no_mangle]
pub unsafe extern "C" fn unienc_new_encoding_system(
    video_options: *const VideoEncoderOptionsNative,
    audio_options: *const AudioEncoderOptionsNative,
) -> *mut PlatformEncodingSystem {
    unsafe {
        let system = PlatformEncodingSystem::new(&*video_options, &*audio_options);
        Box::into_raw(Box::new(system))
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_free_encoding_system(system: *mut PlatformEncodingSystem) {
    if !system.is_null() {
        unsafe {
            let _ = Box::from_raw(system);
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_new_video_encoder(
    system: *const PlatformEncodingSystem,
    input_out: *mut *const Mutex<Option<VideoEncoderInput>>,
    output_out: *mut *const Mutex<Option<VideoEncoderOutput>>,
) -> bool {
    if system.is_null() {
        return false;
    }

    unsafe {
        match (*system).new_video_encoder() {
            Ok(encoder) => match encoder.get() {
                Ok((input, output)) => {
                    *input_out = Arc::into_raw(Arc::new(Mutex::new(Some(input))));
                    *output_out = Arc::into_raw(Arc::new(Mutex::new(Some(output))));
                    true
                }
                Err(_) => false,
            },
            Err(_) => false,
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_new_audio_encoder(
    system: *const PlatformEncodingSystem,
    input_out: *mut *const Mutex<Option<AudioEncoderInput>>,
    output_out: *mut *const Mutex<Option<AudioEncoderOutput>>,
) -> bool {
    if system.is_null() {
        return false;
    }

    unsafe {
        match (*system).new_audio_encoder() {
            Ok(encoder) => match encoder.get() {
                Ok((input, output)) => {
                    *input_out = Arc::into_raw(Arc::new(Mutex::new(Some(input))));
                    *output_out = Arc::into_raw(Arc::new(Mutex::new(Some(output))));
                    true
                }
                Err(_) => false,
            },
            Err(_) => false,
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn unienc_new_muxer(
    system: *const PlatformEncodingSystem,
    output_path: *const c_char,
    video_input_out: *mut *const Mutex<Option<VideoMuxerInput>>,
    audio_input_out: *mut *const Mutex<Option<AudioMuxerInput>>,
    completion_handle_out: *mut *const Mutex<Option<MuxerCompletionHandle>>,
) -> bool {
    if system.is_null() || output_path.is_null() {
        return false;
    }

    unsafe {
        let path_str = match CStr::from_ptr(output_path).to_str() {
            Ok(s) => s,
            Err(_) => return false,
        };
        let path = Path::new(path_str);

        match (*system).new_muxer(path) {
            Ok(muxer) => {
                match muxer.get_inputs() {
                    Ok((video_input, audio_input, completion_handle)) => {
                        // Box the completion handle and store as raw pointer

                        *video_input_out = Arc::into_raw(Arc::new(Mutex::new(Some(video_input))));
                        *audio_input_out = Arc::into_raw(Arc::new(Mutex::new(Some(audio_input))));
                        *completion_handle_out =
                            Arc::into_raw(Arc::new(Mutex::new(Some(completion_handle))));
                        true
                    }
                    Err(_) => false,
                }
            }
            Err(_) => false,
        }
    }
}

fn arc_from_raw_retained<T: Send>(ptr: *const T) -> Arc<T> {
    let arc = unsafe { Arc::from_raw(ptr) };
    let _ = Arc::into_raw(arc.clone());
    arc
}

fn arc_from_raw<T: Send>(ptr: *const T) -> Arc<T> {
    unsafe { Arc::from_raw(ptr) }
}

trait ApplyCallback<Callback> {
    fn apply_callback(&self, callback: Callback, user_data: SendPtr<c_void>);
}

impl ApplyCallback<UniencCallback> for UniencError {
    fn apply_callback(&self, callback: UniencCallback, user_data: SendPtr<c_void>) {
        self.with_native(|native| unsafe { callback(user_data.into(), *native) });
    }
}

impl ApplyCallback<UniencCallback> for Result<(), UniencError> {
    fn apply_callback(&self, callback: UniencCallback, user_data: SendPtr<c_void>) {
        match self {
            Ok(()) => unsafe { callback(user_data.into(), UniencErrorNative::SUCCESS) },
            Err(err) => err.with_native(|native| unsafe { callback(user_data.into(), *native) }),
        }
    }
}

impl ApplyCallback<UniencDataCallback> for UniencError {
    fn apply_callback(&self, callback: UniencDataCallback, user_data: SendPtr<c_void>) {
        self.with_native(|native| unsafe {
            callback(
                user_data.into(),
                std::ptr::null_mut(),
                0,
                0.0,
                false,
                *native,
            )
        });
    }
}
impl<T: EncodedData> ApplyCallback<UniencDataCallback> for Result<Option<T>, UniencError> {
    fn apply_callback(&self, callback: UniencDataCallback, user_data: SendPtr<c_void>) {
        let result = match self {
            Ok(Some(data)) => {
                let timestamp = data.timestamp();
                let is_key = data.is_key();
                match bincode::encode_to_vec(data, bincode::config::standard()) {
                    Ok(serialized) => Ok((serialized, timestamp, is_key)),
                    Err(_) => Err(UniencError::encoding_error(
                        "Failed to serialize encoded data",
                    )),
                }
            }
            Ok(None) => Ok((vec![], 0.0, false)),
            Err(e) => Err(e.clone()),
        };

        match result {
            Ok(data) => unsafe {
                let (serialized, timestamp, is_key) = data;

                callback(
                    user_data.into(),
                    serialized.as_ptr(),
                    serialized.len(),
                    timestamp,
                    is_key,
                    UniencErrorNative::SUCCESS,
                )
            },
            Err(err) => err.with_native(|native| unsafe {
                callback(
                    user_data.into(),
                    std::ptr::null_mut(),
                    0,
                    0.0,
                    false,
                    *native,
                )
            }),
        }
    }
}
