use std::os::raw::{c_int, c_void};
use unienc::{EncodingSystem, GraphicsEventIssuer};
use crate::*;

pub struct UniencGraphicsEventIssuer {
    func: UniencIssueGraphicsEventCallback,
    weak_runtime: WeakRuntime,
}

impl UniencGraphicsEventIssuer {
    pub fn new(func: UniencIssueGraphicsEventCallback, weak_runtime: WeakRuntime) -> Self {
        Self { func, weak_runtime }
    }
}

struct GraphicsEventContext {
    callback: Box<dyn FnOnce() + Send>,
    weak_runtime: WeakRuntime,
}

impl GraphicsEventIssuer for UniencGraphicsEventIssuer {
    fn issue_graphics_event(&self, callback: Box<dyn FnOnce() + Send>, event_id: c_int) {

        let user_data = Box::into_raw(Box::new(GraphicsEventContext{ callback, weak_runtime: self.weak_runtime.clone() })) as *mut c_void;
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
    let context = Box::<GraphicsEventContext>::from_raw(user_data as *mut _);
    let Some(runtime) = context.weak_runtime.upgrade() else {
        println!("Failed to upgrade runtime in graphics event callback");
        return;
    };
    let _guard = runtime.enter();
    let callback = context.callback;
    callback();
}

#[no_mangle]
pub unsafe extern "C" fn unienc_is_blit_supported(system: *const PlatformEncodingSystem) -> bool {
    (&*system).is_blit_supported()
}

#[no_mangle]
pub unsafe extern "C" fn unienc_free_graphics_event_context(
    context: *mut c_void,
) {
    if !context.is_null() {
        unsafe {
            let _ = Box::<Box::<dyn FnOnce() + Send>>::from_raw(context as *mut Box<dyn FnOnce() + Send>);
        }
    }
}