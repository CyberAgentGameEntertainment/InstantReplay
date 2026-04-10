use std::os::raw::c_void;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_free_graphics_event_context(
    context: *mut c_void,
) {
    #[cfg(feature = "unity")]
    if !context.is_null() {
        unsafe {
            let ctx = Box::from_raw(context as *mut crate::unity::GraphicsEventContext);
            // rust_context is a raw pointer to RustGraphicsEventData — drop it too.
            ctx.drop_rust_context();
        }
    }
}
