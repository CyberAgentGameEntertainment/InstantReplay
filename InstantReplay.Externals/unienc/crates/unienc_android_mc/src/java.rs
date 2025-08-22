use std::sync::Arc;

use jni::{objects::{GlobalRef, JObject, JString}, AttachGuard, JNIEnv, JavaVM};
use anyhow::{Context, Result};


/// Get the global JavaVM instance
pub fn get_java_vm() -> Result<&'static JavaVM> {
    crate::JAVA_VM
        .get()
        .ok_or_else(|| anyhow::anyhow!("JavaVM not initialized"))
}

/// Attach current thread to JVM and get JNIEnv
pub fn attach_current_thread() -> Result<AttachGuard<'static>> {
    let vm = get_java_vm()?;
    vm.attach_current_thread()
        .map_err(|e| anyhow::anyhow!("Failed to attach current thread to JVM: {:?}", e))
}

/// Thread-safe wrapper for Java GlobalRef
pub struct SafeGlobalRef {
    inner: Arc<GlobalRef>,
}

impl SafeGlobalRef {
    pub fn new(env: &JNIEnv, obj: JObject) -> Result<Self> {
        let global_ref = env
            .new_global_ref(obj)
            .context("Failed to create global reference")?;
        Ok(Self {
            inner: Arc::new(global_ref),
        })
    }

    pub fn as_obj(&self) -> &JObject {
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

/// Convert JNI exception to Rust error
pub fn check_jni_exception(env: &JNIEnv) -> Result<()> {
    if env.exception_check()? {
        env.exception_describe()?;
        env.exception_clear()?;
        return Err(anyhow::anyhow!("JNI exception occurred"));
    }
    Ok(())
}

/// Helper to call Java void methods with error handling
pub fn call_void_method(
    env: &JNIEnv,
    obj: &JObject,
    name: &str,
    sig: &str,
    args: &[jni::objects::JValue],
) -> Result<()> {
    let env = unsafe { &mut env.unsafe_clone() };
    env.call_method(obj, name, sig, args)
        .context(format!("Failed to call method: {}", name))?;
    check_jni_exception(env)?;
    Ok(())
}

/// Helper to call Java methods returning int
pub fn call_int_method(
    env: &mut JNIEnv,
    obj: &JObject,
    name: &str,
    sig: &str,
    args: &[jni::objects::JValue],
) -> Result<jni::sys::jint> {
    let result = env
        .call_method(obj, name, sig, args)
        .context(format!("Failed to call method: {}", name))?;
    check_jni_exception(env)?;
    result.i().context("Expected int return value")
}

/// Helper to call Java methods returning object
pub fn call_object_method<'a>(
    env: &mut JNIEnv<'a>,
    obj: &JObject,
    name: &str,
    sig: &str,
    args: &[jni::objects::JValue],
) -> Result<JObject<'a>> {
    let result = env
        .call_method(obj, name, sig, args)
        .context(format!("Failed to call method: {}", name))?;
    check_jni_exception(env)?;
    result.l().context("Expected object return value")
}

/// Helper to get int field
pub fn get_int_field(env: &mut JNIEnv, obj: &JObject, name: &str) -> Result<jni::sys::jint> {
    let result = env
        .get_field(obj, name, "I")
        .context(format!("Failed to get field: {}", name))?;
    check_jni_exception(env)?;
    result.i().context("Expected int field")
}

/// Helper to get long field
pub fn get_long_field(env: &mut JNIEnv, obj: &JObject, name: &str) -> Result<jni::sys::jlong> {
    let result = env
        .get_field(obj, name, "J")
        .context(format!("Failed to get field: {}", name))?;
    check_jni_exception(env)?;
    result.j().context("Expected long field")
}

/// Convert Rust string to Java string
pub fn to_java_string<'a>(env: &JNIEnv<'a>, s: &str) -> Result<JString<'a>> {
    env.new_string(s).context("Failed to create Java string")
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
        return Err(anyhow::anyhow!("Buffer is not a direct buffer"));
    }

    // Get buffer capacity
    let capacity = env.get_direct_buffer_capacity(byte_buffer)?;

    // Get current position
    let position = env.call_method(buffer, "position", "()I", &[])?.i()? as usize;

    Ok((base_address, capacity, position))
}
