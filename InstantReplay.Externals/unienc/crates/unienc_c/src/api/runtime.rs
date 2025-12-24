
use crate::*;

#[no_mangle]
pub unsafe extern "C" fn unienc_new_runtime() -> *mut Runtime {
    let runtime = Runtime::new().unwrap();
    Box::into_raw(Box::new(runtime))
}

#[no_mangle]
pub unsafe extern "C" fn unienc_drop_runtime(runtime: *mut Runtime) {
    drop(Box::from_raw(runtime));
}