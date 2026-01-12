
use crate::*;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_new_runtime() -> *mut Runtime {
    let runtime = Runtime::new().unwrap();
    Box::into_raw(Box::new(runtime))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_drop_runtime(runtime: *mut Runtime) {
    drop(unsafe { Box::from_raw(runtime) });
}