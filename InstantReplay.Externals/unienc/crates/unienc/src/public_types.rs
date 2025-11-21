use crate::{UniencErrorNative, UniencSampleData, blit::UniencBlitTargetData};

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

// These are unused but required to let csbindgen generate the binding for specific types.
#[no_mangle]
pub unsafe extern "C" fn unienc_dummy(_error_kind: UniencErrorKind, _error_native: UniencErrorNative, _sample: UniencSampleData, _blit: UniencBlitTargetData) {
    
}
