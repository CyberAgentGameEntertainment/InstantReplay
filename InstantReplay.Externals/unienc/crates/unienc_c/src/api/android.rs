use std::ffi::c_void;
use std::ffi::{c_int, CString};
use std::io::{BufRead, BufReader, PipeReader};
use std::os::unix::io::FromRawFd;
use std::thread;
use thiserror::Error;

use ndk_sys::__android_log_write;

const ANDROID_LOG_INFO: c_int = 4;

#[derive(Error, Debug)]
pub enum AndroidApiError {
    #[error("Failed to create pipe")]
    PipeCreationFailed,
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn JNI_OnLoad(vm: *mut c_void, reserved: *mut c_void) -> c_int {
    set_stdout_redirect("unienc").unwrap_or_else(|e| {
        log_to_logcat("unienc", &format!("Failed to redirect stdout: {}", e));
    });
    unienc::android::set_java_vm(vm as *mut _, reserved)
}

pub fn log_to_logcat(tag: &str, message: &str) {
    let tag = CString::new(tag).unwrap();
    let message = CString::new(message).unwrap();
    unsafe {
        __android_log_write(ANDROID_LOG_INFO, tag.as_ptr(), message.as_ptr());
    }
}

// redirect stdout to logcat
pub unsafe fn set_stdout_redirect(log_tag: &'static str) -> Result<(), AndroidApiError> {
    let mut pipe_fds = [0; 2];
    if libc::pipe(pipe_fds.as_mut_ptr()) == -1 {
        return Err(AndroidApiError::PipeCreationFailed);
    }
    libc::dup2(pipe_fds[1], libc::STDOUT_FILENO);
    libc::dup2(pipe_fds[1], libc::STDERR_FILENO);

    thread::spawn(move || {
        let pipe_read_end = PipeReader::from_raw_fd(pipe_fds[0]);
        let reader = BufReader::new(pipe_read_end);

        for line in reader.lines().map_while(|r| r.ok()) {
            log_to_logcat(log_tag, &line);
        }
    });

    libc::close(pipe_fds[1]);

    Ok(())
}