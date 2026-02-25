use std::ffi::{CStr, c_char};

unsafe extern "C" {
    fn emscripten_run_script(script: *const c_char);
    fn emscripten_run_script_int(script: *const c_char) -> i32;
}

pub fn run_script(script: &CStr) {
    unsafe {
        emscripten_run_script(script.as_ptr());
    }
}

pub fn run_script_int(script: &CStr) -> i32 {
    unsafe { emscripten_run_script_int(script.as_ptr()) }
}
