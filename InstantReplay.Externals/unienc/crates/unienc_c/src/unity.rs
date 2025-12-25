use unity_native_plugin::graphics::RenderingEventAndData;
use std::os::raw::{c_int, c_void};
use unienc::{EncodingSystem, GraphicsEventIssuer};
use crate::*;

pub type UniencIssueGraphicsEventCallback =
unsafe extern "C" fn(func: RenderingEventAndData, event_id: i32, user_data: *mut c_void);

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
    let context = unsafe { Box::<GraphicsEventContext>::from_raw(user_data as *mut _) };
    let Some(runtime) = context.weak_runtime.upgrade() else {
        println!("Failed to upgrade runtime in graphics event callback");
        return;
    };
    let _guard = runtime.enter();
    let callback = context.callback;
    callback();
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_is_blit_supported(system: *const PlatformEncodingSystem) -> bool {
    unsafe {&*system}.is_blit_supported()
}

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

#[cfg(not(target_os = "ios"))]
mod entry_points {
    use unienc::unity::UnityPlugin;
    use crate::platform::PlatformEncodingSystem;

    unity_native_plugin::unity_native_plugin_entry_point! {
        fn unity_plugin_load(interfaces: &unity_native_plugin::interface::UnityInterfaces) {
            PlatformEncodingSystem::unity_plugin_load(interfaces);
        }
        fn unity_plugin_unload() {
            PlatformEncodingSystem::unity_plugin_unload();
        }
    }
}

// statically linked for iOS
// we add `unienc_` prefix to avoid name collision with other plugins
#[cfg(target_os = "ios")]
mod entry_points {
    use unienc::unity::UnityPlugin;
    use crate::platform::PlatformEncodingSystem;
    #[unsafe(no_mangle)]
    #[allow(non_snake_case)]
    extern "system" fn unienc_UnityPluginLoad(
        interfaces: *mut unity_native_plugin::IUnityInterfaces,
    ) {
        unity_native_plugin::interface::UnityInterfaces::set_native_unity_interfaces(interfaces);
        PlatformEncodingSystem::unity_plugin_load(unity_native_plugin::interface::UnityInterfaces::get());
    }

    #[unsafe(no_mangle)]
    #[allow(non_snake_case)]
    extern "system" fn unienc_UnityPluginUnload() {
        PlatformEncodingSystem::unity_plugin_unload();
        unity_native_plugin::interface::UnityInterfaces::set_native_unity_interfaces(std::ptr::null_mut());
    }
}

