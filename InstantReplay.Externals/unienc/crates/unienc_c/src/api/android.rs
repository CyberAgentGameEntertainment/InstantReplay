use std::ffi::{c_int, c_void};

#[unsafe(no_mangle)]
pub unsafe extern "C" fn JNI_OnLoad(vm: *mut c_void, reserved: *mut c_void) -> c_int {
    unsafe {
        // Records go to logcat under the `unienc` tag from here on. This deliberately does not
        // redirect the process-wide stdout/stderr: that captured every other library's output as
        // well, and collided with anything else in the process doing the same.
        crate::logging::init();
        unienc::android::set_java_vm(vm as *mut _, reserved)
    }
}
