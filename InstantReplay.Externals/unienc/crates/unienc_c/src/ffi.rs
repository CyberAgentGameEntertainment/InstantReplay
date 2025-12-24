use std::ffi::{c_char, CString};
use std::ops::Deref;
use std::os::raw::c_void;
use std::sync::Arc;
use unity_native_plugin::graphics::RenderingEventAndData;
use unienc::{CategorizedError, EncodedData, ErrorCategory, UniencSampleKind};
use crate::*;


// Callback types for async operations
pub type UniencCallback = unsafe extern "C" fn(user_data: *mut c_void, error: UniencErrorNative);
pub type UniencDataCallback<Data> =
unsafe extern "C" fn(data: Data, user_data: *mut c_void, error: UniencErrorNative);
pub type UniencIssueGraphicsEventCallback =
unsafe extern "C" fn(func: RenderingEventAndData, event_id: i32, user_data: *mut c_void);

// Send-safe wrappers for raw pointers
#[repr(transparent)]
pub struct SendPtr<T>(*mut T);

impl<T> Clone for SendPtr<T> {
    fn clone(&self) -> Self {
        *self
    }
}
impl<T> Copy for SendPtr<T> {}

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

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum UniencErrorKind {
    Success = 0,
    Error = 1,
    InitializationError = 2,
    ConfigurationError = 3,
    ResourceAllocationError = 4,
    EncodingError = 5,
    MuxingError = 6,
    CommunicationError = 7,
    TimeoutError = 8,
    InvalidInput = 9,
    PlatformError = 10,
}

impl From<ErrorCategory> for UniencErrorKind {
    fn from(category: ErrorCategory) -> Self {
        match category {
            ErrorCategory::General => UniencErrorKind::Error,
            ErrorCategory::Initialization => UniencErrorKind::InitializationError,
            ErrorCategory::Configuration => UniencErrorKind::ConfigurationError,
            ErrorCategory::ResourceAllocation => UniencErrorKind::ResourceAllocationError,
            ErrorCategory::Encoding => UniencErrorKind::EncodingError,
            ErrorCategory::Muxing => UniencErrorKind::MuxingError,
            ErrorCategory::Communication => UniencErrorKind::CommunicationError,
            ErrorCategory::Timeout => UniencErrorKind::TimeoutError,
            ErrorCategory::InvalidInput => UniencErrorKind::InvalidInput,
            ErrorCategory::Platform => UniencErrorKind::PlatformError,
        }
    }
}

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

    /// Convert a CommonError to UniencError using the error's category
    pub fn from_common(err: unienc::CommonError) -> Self {
        let kind = UniencErrorKind::from(err.category());
        let message = err.to_string();
        Self {
            kind,
            message: Some(message),
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

pub fn arc_from_raw_retained<T: Send>(ptr: *const T) -> Arc<T> {
    let arc = unsafe { Arc::from_raw(ptr) };
    let clone = arc.clone();
    let _ = Arc::into_raw(arc);
    clone
}

pub fn arc_from_raw<T: Send>(ptr: *const T) -> Arc<T> {
    unsafe { Arc::from_raw(ptr) }
}

pub trait ApplyCallback<Callback> {
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

impl<Data: Default> ApplyCallback<UniencDataCallback<Data>> for UniencError {
    fn apply_callback(&self, callback: UniencDataCallback<Data>, user_data: SendPtr<c_void>) {
        self.with_native(|native| unsafe { callback(Data::default(), user_data.into(), *native) });
    }
}
impl<T: EncodedData> ApplyCallback<UniencDataCallback<UniencSampleData>>
for Result<Option<T>, UniencError>
{
    fn apply_callback(
        &self,
        callback: UniencDataCallback<UniencSampleData>,
        user_data: SendPtr<c_void>,
    ) {
        let result = match self {
            Ok(Some(data)) => {
                let timestamp = data.timestamp();
                let kind = data.kind();
                match bincode::encode_to_vec(data, bincode::config::standard()) {
                    Ok(serialized) => Ok((serialized, timestamp, kind)),
                    Err(_) => Err(UniencError::encoding_error(
                        "Failed to serialize encoded data",
                    )),
                }
            }
            Ok(None) => Ok((vec![], 0.0, UniencSampleKind::Interpolated)),
            Err(e) => Err(e.clone()),
        };

        match result {
            Ok(data) => unsafe {
                let (serialized, timestamp, kind) = data;

                callback(
                    UniencSampleData {
                        data: serialized.as_ptr(),
                        size: serialized.len(),
                        timestamp,
                        kind,
                    },
                    user_data.into(),
                    UniencErrorNative::SUCCESS,
                )
            },
            Err(err) => err.with_native(|native| unsafe {
                callback(
                    UniencSampleData::default(),
                    user_data.into(),
                    *native,
                )
            }),
        }
    }
}

// These are unused but required to let csbindgen generate the binding for specific types.
#[no_mangle]
pub unsafe extern "C" fn unienc_dummy(_error_kind: UniencErrorKind, _error_native: UniencErrorNative, _sample: UniencSampleData) {

}