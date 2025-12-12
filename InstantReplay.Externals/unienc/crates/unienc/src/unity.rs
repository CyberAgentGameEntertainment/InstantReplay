
#[cfg(not(target_os = "ios"))]
mod entry_points {
    use unienc_common::EncodingSystem;
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
    use unienc_common::EncodingSystem;
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

