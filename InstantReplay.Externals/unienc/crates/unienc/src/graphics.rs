use std::os::raw::{c_int, c_void};
use unienc_common::GraphicsEventIssuer;
use crate::UniencGraphicsEventIssuer;

impl GraphicsEventIssuer for UniencGraphicsEventIssuer {
    fn issue_graphics_event(&self, callback: impl FnOnce() + Send, event_id: c_int) {
        let callback: Box<dyn FnOnce() + Send> = Box::new(callback);
        let user_data = Box::into_raw(callback) as *mut c_void;
        unsafe {
            (self.func)(
                Some(graphics_event_callback_trampoline),
                event_id,
                user_data,
            )
        }
    }
}

unsafe extern "system" fn graphics_event_callback_trampoline(
    _event_id: c_int,
    user_data: *mut c_void,
) {
    let callback: Box<Box<dyn FnOnce() + Send>> = Box::from_raw(user_data as *mut _);
    callback();
}

#[no_mangle]
pub unsafe extern "C" fn unienc_free_graphics_event_context(
    context: *mut c_void,
) {
    if !context.is_null() {
        unsafe {
            let _ = Box::from_raw(context);
        }
    }
}