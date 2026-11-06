//! Calls from native threads into the managed host (C# callbacks).
//!
//! # Persistent IL2CPP attachment of worker threads
//!
//! When IL2CPP invokes a managed callback on a thread that is not attached to it, the reverse
//! P/Invoke wrapper attaches the thread for the duration of the call and detaches it afterwards.
//! Each attachment creates a new `il2cpp::os::Thread`, and a detached one stays in the list of alive
//! threads until the finalizer of the corresponding managed thread object runs. At shutdown,
//! `il2cpp::os::Thread::Shutdown` waits up to 10 ms for each of them, which stalls the exit of the
//! application for tens of seconds after a long recording.
//!
//! To avoid it, the worker threads of the runtime attach themselves to IL2CPP once, on the first
//! managed callback, and stay attached. The reverse P/Invoke wrapper then finds the thread already
//! attached and does nothing.
//!
//! A thread that is attached to IL2CPP must be detached before `il2cpp::vm::Thread::AbortAllThreads`
//! runs at shutdown; otherwise it waits forever for the thread to exit. Moreover, no managed
//! callback may be invoked from a native thread after that point. [`shutdown`] disables the managed
//! callbacks and detaches every worker, and is called from `AppDomain.ProcessExit`, which IL2CPP
//! raises before aborting the threads.

use std::sync::atomic::{AtomicBool, Ordering};

static CALLBACKS_ENABLED: AtomicBool = AtomicBool::new(true);

/// Invokes a managed callback through `f` unless the managed callbacks have been disabled by
/// [`shutdown`]. Returns `None` if the callback has been skipped.
///
/// The worker threads of the runtime are polled only between tasks for [`shutdown`]; therefore a
/// callback invoked on a worker thread never overlaps with the detachment of that thread.
pub fn invoke<R>(f: impl FnOnce() -> R) -> Option<R> {
    if !CALLBACKS_ENABLED.load(Ordering::Acquire) {
        return None;
    }
    #[cfg(feature = "multi-thread")]
    il2cpp::attach_current_worker();
    Some(f())
}

/// Called on a worker thread of the runtime before it exits.
pub fn on_worker_stop() {
    #[cfg(feature = "multi-thread")]
    il2cpp::detach_current_thread();
}

/// Disables the managed callbacks and detaches every worker thread of `runtime` from IL2CPP.
///
/// Returns `true` if every worker has been detached within the timeout.
pub fn shutdown(runtime: &crate::Runtime, timeout: std::time::Duration) -> bool {
    CALLBACKS_ENABLED.store(false, Ordering::Release);

    #[cfg(feature = "multi-thread")]
    {
        // Every worker runs the job after the task it is currently polling, which completes any
        // callback in progress on that worker.
        runtime.broadcast(il2cpp::detach_current_thread, timeout)
    }
    #[cfg(not(feature = "multi-thread"))]
    {
        let _ = (runtime, timeout);
        true
    }
}

/// Enables the persistent IL2CPP attachment of the worker threads. Returns `false` if the IL2CPP
/// API cannot be resolved in the current process.
pub fn enable_il2cpp_thread_attach() -> bool {
    #[cfg(feature = "multi-thread")]
    {
        il2cpp::enable()
    }
    #[cfg(not(feature = "multi-thread"))]
    {
        false
    }
}

#[cfg(feature = "multi-thread")]
mod il2cpp {
    use std::cell::Cell;
    use std::ffi::{CStr, c_void};
    use std::sync::OnceLock;

    type DomainGet = unsafe extern "C" fn() -> *mut c_void;
    type ThreadAttach = unsafe extern "C" fn(domain: *mut c_void) -> *mut c_void;
    type ThreadDetach = unsafe extern "C" fn(thread: *mut c_void);

    struct Api {
        domain_get: DomainGet,
        thread_attach: ThreadAttach,
        thread_detach: ThreadDetach,
    }

    static API: OnceLock<Option<Api>> = OnceLock::new();

    thread_local! {
        /// The `Il2CppThread*` that the current thread has been attached as by [`attach_current_worker`].
        static ATTACHED_THREAD: Cell<*mut c_void> = const { Cell::new(std::ptr::null_mut()) };
    }

    pub fn enable() -> bool {
        let enabled = API.get_or_init(resolve).is_some();
        if enabled {
            log::debug!("Worker threads will be attached to IL2CPP persistently");
        } else {
            log::warn!(
                "Failed to resolve the IL2CPP API; worker threads will not be attached persistently"
            );
        }
        enabled
    }

    pub fn attach_current_worker() {
        let Some(Some(api)) = API.get() else {
            return;
        };
        if !crate::thread_pool::is_worker_thread() || !ATTACHED_THREAD.get().is_null() {
            return;
        }
        let thread = unsafe { (api.thread_attach)((api.domain_get)()) };
        ATTACHED_THREAD.set(thread);
    }

    pub fn detach_current_thread() {
        let thread = ATTACHED_THREAD.replace(std::ptr::null_mut());
        if thread.is_null() {
            return;
        }
        if let Some(Some(api)) = API.get() {
            unsafe { (api.thread_detach)(thread) };
        }
    }

    fn resolve() -> Option<Api> {
        unsafe {
            Some(Api {
                domain_get: std::mem::transmute::<*mut c_void, DomainGet>(symbol(
                    c"il2cpp_domain_get",
                )?),
                thread_attach: std::mem::transmute::<*mut c_void, ThreadAttach>(symbol(
                    c"il2cpp_thread_attach",
                )?),
                thread_detach: std::mem::transmute::<*mut c_void, ThreadDetach>(symbol(
                    c"il2cpp_thread_detach",
                )?),
            })
        }
    }

    #[cfg(windows)]
    fn symbol(name: &CStr) -> Option<*mut c_void> {
        unsafe extern "system" {
            fn GetModuleHandleW(module_name: *const u16) -> *mut c_void;
            fn GetProcAddress(
                module: *mut c_void,
                proc_name: *const std::ffi::c_char,
            ) -> *mut c_void;
        }

        let module_name: Vec<u16> = "GameAssembly.dll".encode_utf16().chain([0]).collect();
        let module = unsafe { GetModuleHandleW(module_name.as_ptr()) };
        if module.is_null() {
            return None;
        }
        let address = unsafe { GetProcAddress(module, name.as_ptr()) };
        (!address.is_null()).then_some(address)
    }

    #[cfg(unix)]
    fn symbol(name: &CStr) -> Option<*mut c_void> {
        // The library that contains IL2CPP on each platform. It is loaded with RTLD_LOCAL on some
        // platforms, in which case the symbols are not visible through RTLD_DEFAULT.
        const LIBRARIES: &[&CStr] = &[c"libil2cpp.so", c"GameAssembly.so", c"GameAssembly.dylib"];

        for library in LIBRARIES {
            let handle =
                unsafe { libc::dlopen(library.as_ptr(), libc::RTLD_NOW | libc::RTLD_NOLOAD) };
            if handle.is_null() {
                continue;
            }
            let address = unsafe { libc::dlsym(handle, name.as_ptr()) };
            // Balance the reference count incremented by dlopen; the library stays loaded.
            unsafe { libc::dlclose(handle) };
            if !address.is_null() {
                return Some(address);
            }
        }

        // IL2CPP is linked statically into the executable or the framework (e.g. iOS).
        let address = unsafe { libc::dlsym(libc::RTLD_DEFAULT, name.as_ptr()) };
        (!address.is_null()).then_some(address)
    }

    #[cfg(not(any(windows, unix)))]
    fn symbol(_name: &CStr) -> Option<*mut c_void> {
        None
    }
}
