use crate::UniencErrorNative;

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum UniencErrorKind {
    Success = 0,
    Error = 1, // Keep for backward compatibility
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

// Audio encoder input/output functions
#[no_mangle]
pub unsafe extern "C" fn unienc_error_kind_is_success(error: UniencErrorKind) -> bool {
    error == UniencErrorKind::Success
}
#[no_mangle]
pub unsafe extern "C" fn unienc_error_is_success(error: UniencErrorNative) -> bool {
    error.kind == UniencErrorKind::Success
}
