//! Android device harness for the shared end-to-end test.
//!
//! The MediaCodec backend needs a `JavaVM`, and the only way to obtain one is to
//! be loaded by a JVM. A bare executable pushed with `adb` therefore cannot run
//! the encoders, however convenient that would be.
//!
//! MediaCodec and MediaMuxer do not need an `Activity` or a `Context` though, so
//! a JVM is the *only* thing missing, and `app_process` provides one from a
//! shell. That is why this is a library loaded by a small Java shim rather than
//! an instrumented test inside an APK: no Gradle project, no packaging, and the
//! same command works against an emulator and a real device.
//!
//! See `scripts/android-device-test.sh`.

use std::ffi::{c_int, c_void};
use std::path::PathBuf;

use jni::JNIEnv;
use jni::objects::{JClass, JString};

use unienc_testkit::E2eConfig;

/// Called by the JVM when the Java shim loads this library.
///
/// Handing the `JavaVM` to `unienc_android_mc` here is what makes the encoders
/// usable at all; without it every call fails with `JavaVM not initialized`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn JNI_OnLoad(vm: *mut c_void, reserved: *mut c_void) -> c_int {
    unsafe { unienc::android::set_java_vm(vm as *mut _, reserved) }
}

/// Runs the harness and returns a process exit status: zero when everything the
/// harness checks holds, one when it does not.
///
/// Both the description of a successful output and the reason for a failure go
/// to stdout, which under `app_process` is the shell that invoked it.
#[unsafe(no_mangle)]
pub extern "system" fn Java_jp_co_cyberagent_unienc_harness_Harness_run(
    mut env: JNIEnv,
    _class: JClass,
    output_path: JString,
) -> c_int {
    let output_path: String = match env.get_string(&output_path) {
        Ok(path) => path.into(),
        Err(error) => {
            println!("harness: cannot read the output path argument: {error}");
            return 1;
        }
    };

    match unienc_testkit::run_and_verify(&E2eConfig::default(), &PathBuf::from(output_path)) {
        Ok(description) => {
            println!("harness: ok\n{description}");
            0
        }
        Err(message) => {
            println!("harness: FAILED\n{message}");
            1
        }
    }
}
