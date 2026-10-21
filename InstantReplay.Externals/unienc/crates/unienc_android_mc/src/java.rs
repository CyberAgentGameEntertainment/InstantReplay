use std::ops::{Deref, DerefMut};
use std::sync::Arc;

use crate::bindings;
use crate::error::{AndroidError, Result};
use jni::{
    JNIEnv, JavaVM,
    objects::{GlobalRef, JObject, JString},
};

/// Get the global JavaVM instance
pub fn get_java_vm() -> Result<&'static JavaVM> {
    crate::JAVA_VM
        .get()
        .ok_or(AndroidError::JavaVmNotInitialized)
}

/// Attach current thread to JVM and get JNIEnv
///
/// The thread stays attached until it terminates. A scoped attachment would detach the thread
/// when its guard is dropped, so a `GlobalRef` dropped after that point (e.g. a field of a
/// struct whose `Drop` impl attaches the thread) would be deleted on a detached thread, and
/// every JNI call would pay for an attach and a detach.
///
/// Since the thread is no longer detached, the returned guard pushes a local reference frame and
/// pops it on drop instead, so that local references created in the scope are released.
pub fn attach_current_thread() -> Result<LocalFrameEnv> {
    let vm = get_java_vm()?;
    let env = vm
        .attach_current_thread_permanently()
        .map_err(|e| AndroidError::JvmAttachFailed(format!("{:?}", e)))?;
    env.push_local_frame(LOCAL_FRAME_CAPACITY)?;
    Ok(LocalFrameEnv { env })
}

const LOCAL_FRAME_CAPACITY: i32 = 16;

/// JNIEnv of an attached thread with a local reference frame that is popped on drop
///
/// Local references created through this env must not outlive it. Use `SafeGlobalRef` to keep a
/// Java object beyond the scope.
pub struct LocalFrameEnv {
    env: JNIEnv<'static>,
}

impl Deref for LocalFrameEnv {
    type Target = JNIEnv<'static>;

    fn deref(&self) -> &Self::Target {
        &self.env
    }
}

impl DerefMut for LocalFrameEnv {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.env
    }
}

impl Drop for LocalFrameEnv {
    fn drop(&mut self) {
        // SAFETY: The frame was pushed in `attach_current_thread`, and the local references
        // created in it are not used after the guard is dropped.
        if let Err(e) = unsafe { self.env.pop_local_frame(&JObject::null()) } {
            log::error!("failed to pop JNI local frame: {:?}", e);
        }
    }
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
