mod platform;

pub use platform::*;
pub use unienc_common::*;

#[cfg(target_os = "android")]
pub mod android {
    pub use unienc_android_mc::set_java_vm;
}