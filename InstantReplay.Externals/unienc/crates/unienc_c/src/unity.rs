use crate::platform::PlatformEncodingSystem;
use crate::*;
use std::os::raw::{c_int, c_void};
use unienc::unity::UnityPlugin;
use unienc::{EncodingSystem, GraphicsEventIssuer};
use unity_native_plugin::graphics::RenderingEventAndData;

pub type UniencIssueGraphicsEventCallback =
unsafe extern "C" fn(func: RenderingEventAndData, event_id: i32, user_data: *mut c_void, texture_token: usize);

pub struct UniencGraphicsEventIssuer {
    func: UniencIssueGraphicsEventCallback,
    weak_runtime: WeakRuntime,
}

impl UniencGraphicsEventIssuer {
    pub fn new(func: UniencIssueGraphicsEventCallback, weak_runtime: WeakRuntime) -> Self {
        Self { func, weak_runtime }
    }
}

/// Layout shared with C# — C# writes `native_texture_ptr` directly.
/// `rust_context` is an opaque pointer owned by Rust (the callback + runtime).
#[repr(C)]
pub struct GraphicsEventContext {
    pub native_texture_ptr: *mut c_void,
    rust_context: *mut RustGraphicsEventData,
}

impl GraphicsEventContext {
    /// # Safety
    /// Must only be called once. Frees the inner `RustGraphicsEventData`.
    pub(crate) unsafe fn drop_rust_context(self) {
        if !self.rust_context.is_null() {
            unsafe { let _ = Box::from_raw(self.rust_context); }
        }
    }
}

struct RustGraphicsEventData {
    callback: Box<dyn FnOnce(*mut c_void) + Send>,
    weak_runtime: WeakRuntime,
}

impl GraphicsEventIssuer for UniencGraphicsEventIssuer {
    fn issue_graphics_event(&self, callback: Box<dyn FnOnce(*mut c_void) + Send>, event_id: c_int, texture_token: usize) {

        let rust_data = Box::into_raw(Box::new(RustGraphicsEventData {
            callback,
            weak_runtime: self.weak_runtime.clone(),
        }));
        let user_data = Box::into_raw(Box::new(GraphicsEventContext {
            native_texture_ptr: std::ptr::null_mut(),
            rust_context: rust_data,
        })) as *mut c_void;
        unsafe {
            (self.func)(
                Some(graphics_event_callback_trampoline),
                event_id,
                user_data,
                texture_token,
            )
        }
    }
}

unsafe extern "system" fn graphics_event_callback_trampoline(
    _event_id: c_int,
    user_data: *mut c_void,
) {
    let context = unsafe { Box::from_raw(user_data as *mut GraphicsEventContext) };
    let rust_data = unsafe { Box::from_raw(context.rust_context) };
    let Some(runtime) = rust_data.weak_runtime.upgrade() else {
        println!("Failed to upgrade runtime in graphics event callback");
        return;
    };
    let _guard = runtime.enter();
    (rust_data.callback)(context.native_texture_ptr);
}

fn unity_plugin_load(interfaces: &unity_native_plugin::interface::UnityInterfaces) {
    PlatformEncodingSystem::unity_plugin_load(interfaces);
}
fn unity_plugin_unload() {
    PlatformEncodingSystem::unity_plugin_unload();
}

#[cfg(not(target_os = "ios"))]
mod entry_points {
    use std::ffi::c_void;

    #[unsafe(no_mangle)]
    #[allow(non_snake_case)]
    extern "system" fn UnityPluginLoad(interfaces: *mut unity_native_plugin::IUnityInterfaces) {
        #[cfg(feature = "mimalloc")]
        {
            unsafe { mimalloc::unity::init(interfaces as *mut c_void, Some(c"UniEnc"), Some(c"mimalloc")) };
        }
        unity_native_plugin::interface::UnityInterfaces::set_native_unity_interfaces(interfaces);
        super::unity_plugin_load(unity_native_plugin::interface::UnityInterfaces::get());
    }

    #[unsafe(no_mangle)]
    #[allow(non_snake_case)]
    extern "system" fn UnityPluginUnload() {
        super::unity_plugin_unload();
        unity_native_plugin::interface::UnityInterfaces::set_native_unity_interfaces(
            std::ptr::null_mut(),
        );
    }
}

// statically linked for iOS
// we add `unienc_` prefix to avoid name collision with other plugins
#[cfg(target_os = "ios")]
mod entry_points {
    use std::ffi::c_void;
    use crate::platform::PlatformEncodingSystem;
    #[unsafe(no_mangle)]
    #[allow(non_snake_case)]
    extern "system" fn unienc_UnityPluginLoad(
        interfaces: *mut unity_native_plugin::IUnityInterfaces,
    ) {
        #[cfg(feature = "mimalloc")]
        {
            unsafe { mimalloc::unity::init(interfaces as *mut c_void, Some(c"UniEnc"), Some(c"mimalloc")) };
        }
        unity_native_plugin::interface::UnityInterfaces::set_native_unity_interfaces(interfaces);
        super::unity_plugin_load(unity_native_plugin::interface::UnityInterfaces::get());
    }

    #[unsafe(no_mangle)]
    #[allow(non_snake_case)]
    extern "system" fn unienc_UnityPluginUnload() {
        super::unity_plugin_unload();
        unity_native_plugin::interface::UnityInterfaces::set_native_unity_interfaces(
            std::ptr::null_mut(),
        );
    }
}
