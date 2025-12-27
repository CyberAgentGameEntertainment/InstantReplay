use std::os::raw::c_void;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_free_graphics_event_context(
    context: *mut c_void,
) {
    if !context.is_null() {
        unsafe {
            let _ = Box::<Box::<dyn FnOnce() + Send>>::from_raw(context as *mut Box<dyn FnOnce() + Send>);
        }
    }
}