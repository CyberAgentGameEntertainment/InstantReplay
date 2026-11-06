use crate::*;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_new_runtime() -> *mut Runtime {
    // First FFI call on hosts that have no plugin entry point of their own (NuGet package, tests).
    crate::logging::init();
    let runtime = Runtime::new().unwrap();
    Box::into_raw(Box::new(runtime))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_tick_runtime(runtime: *mut Runtime) {
    let Some(runtime) = (unsafe { runtime.as_ref() }) else {
        return;
    };
    runtime.tick();
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_drop_runtime(runtime: *mut Runtime) {
    drop(unsafe { Box::from_raw(runtime) });
}

/// Enables the persistent attachment of the worker threads to IL2CPP. Call it only in an IL2CPP
/// player. Returns `false` if the IL2CPP API cannot be resolved; the worker threads are then attached
/// by IL2CPP for each callback as before.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_enable_il2cpp_thread_attach() -> bool {
    crate::managed::enable_il2cpp_thread_attach()
}

/// Disables every managed callback and detaches the worker threads of the runtime from IL2CPP.
/// Call it when the managed runtime is shutting down; no callback is invoked afterwards, so the
/// operations in progress never complete. Returns `false` if some worker has not been detached
/// within `timeout_ms`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_runtime_shutdown_managed_callbacks(
    runtime: *mut Runtime,
    timeout_ms: u32,
) -> bool {
    let Some(runtime) = (unsafe { runtime.as_ref() }) else {
        return false;
    };
    crate::managed::shutdown(runtime, std::time::Duration::from_millis(timeout_ms as u64))
}
