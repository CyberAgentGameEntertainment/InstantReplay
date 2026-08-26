use std::sync::Arc;

use crate::bindings;
use crate::error::{AndroidError, Result};
use jni::{
    AttachGuard, JNIEnv, JavaVM,
    objects::{GlobalRef, JObject, JString},
};

/// Get the global JavaVM instance
pub fn get_java_vm() -> Result<&'static JavaVM> {
    crate::JAVA_VM
        .get()
        .ok_or(AndroidError::JavaVmNotInitialized)
}

/// Attach current thread to JVM and get JNIEnv
pub fn attach_current_thread() -> Result<AttachGuard<'static>> {
    let vm = get_java_vm()?;
    vm.attach_current_thread()
        .map_err(|e| AndroidError::JvmAttachFailed(format!("{:?}", e)))
}

/// Thread-safe wrapper for Java GlobalRef
pub struct SafeGlobalRef {
    inner: Arc<GlobalRef>,
}

impl SafeGlobalRef {
    pub fn new(env: &JNIEnv, obj: JObject) -> Result<Self> {
        let global_ref = env
            .new_global_ref(obj)
            .map_err(|_| AndroidError::JniGlobalRefFailed)?;
        Ok(Self {
            inner: Arc::new(global_ref),
        })
    }

    pub fn as_obj(&self) -> &JObject<'_> {
        self.inner.as_obj()
    }
}

unsafe impl Send for SafeGlobalRef {}
unsafe impl Sync for SafeGlobalRef {}

impl Clone for SafeGlobalRef {
    fn clone(&self) -> Self {
        Self {
            inner: Arc::clone(&self.inner),
        }
    }
}

/// Convert Rust string to Java string
pub fn to_java_string<'a>(env: &JNIEnv<'a>, s: &str) -> Result<JString<'a>> {
    env.new_string(s)
        .map_err(|_| AndroidError::JniStringCreationFailed)
}

/// Get direct buffer address, capacity and position from DirectByteBuffer
pub fn get_direct_buffer_info(
    env: &mut JNIEnv,
    buffer: &JObject,
) -> Result<(*mut u8, usize, usize)> {
    // Convert JObject to JByteBuffer
    let byte_buffer: &jni::objects::JByteBuffer = buffer.into();

    // Get direct buffer address (always points to the beginning of the buffer)
    let base_address = env.get_direct_buffer_address(byte_buffer)?;
    if base_address.is_null() {
        return Err(AndroidError::NotDirectBuffer);
    }

    // Get buffer capacity
    let capacity = env.get_direct_buffer_capacity(byte_buffer)?;

    // Get current position
    let position = bindings::ByteBuffer::position(env, buffer)? as usize;

    Ok((base_address, capacity, position))
}
