use objc2_core_foundation::CFAllocator;

#[cfg(feature = "mimalloc")]
mod inner {
    use std::ffi::c_void;
    use std::sync::OnceLock;

    use objc2_core_foundation::{
        CFAllocator, CFAllocatorContext, CFIndex, CFOptionFlags, CFRetained,
        kCFAllocatorSystemDefault,
    };

    /// Wrapper to allow storing `CFRetained<CFAllocator>` in a static.
    ///
    /// # Safety
    /// Our custom `CFAllocator` is stateless (no `info` pointer) and backed
    /// entirely by mimalloc, which is thread-safe. The `CFAllocator` object
    /// itself is immutable after creation, so sharing across threads is safe.
    struct SyncAllocator(CFRetained<CFAllocator>);
    unsafe impl Send for SyncAllocator {}
    unsafe impl Sync for SyncAllocator {}

    static ALLOCATOR: OnceLock<SyncAllocator> = OnceLock::new();

    unsafe extern "C-unwind" fn cf_allocate(
        size: CFIndex,
        _hint: CFOptionFlags,
        _info: *mut c_void,
    ) -> *mut c_void {
        unsafe { mimalloc::raw::malloc(size as usize) }
    }

    unsafe extern "C-unwind" fn cf_reallocate(
        ptr: *mut c_void,
        new_size: CFIndex,
        _hint: CFOptionFlags,
        _info: *mut c_void,
    ) -> *mut c_void {
        unsafe { mimalloc::raw::realloc(ptr, new_size as usize) }
    }

    unsafe extern "C-unwind" fn cf_deallocate(ptr: *mut c_void, _info: *mut c_void) {
        unsafe { mimalloc::raw::free(ptr) }
    }

    pub fn get() -> Option<&'static CFAllocator> {
        let wrapper = ALLOCATOR.get_or_init(|| {
            let mut context = CFAllocatorContext {
                version: 0,
                info: std::ptr::null_mut(),
                retain: None,
                release: None,
                copyDescription: None,
                allocate: Some(cf_allocate),
                reallocate: Some(cf_reallocate),
                deallocate: Some(cf_deallocate),
                preferredSize: None,
            };
            SyncAllocator(
                unsafe { CFAllocator::new(kCFAllocatorSystemDefault, &mut context) }
                    .expect("failed to create mimalloc-backed CFAllocator"),
            )
        });
        Some(&wrapper.0)
    }
}

/// Returns a `CFAllocator` backed by mimalloc when the `mimalloc` feature is
/// enabled, or the system default allocator otherwise.
pub fn default() -> Option<&'static CFAllocator> {
    #[cfg(feature = "mimalloc")]
    {
        inner::get()
    }

    #[cfg(not(feature = "mimalloc"))]
    {
        unsafe { objc2_core_foundation::kCFAllocatorDefault }
    }
}
