pub trait UnityPlugin {
    #[allow(unused_variables)]
    #[cfg(feature = "unity")]
    fn unity_plugin_load(interfaces: &unity_native_plugin::interface::UnityInterfaces) {}
    #[cfg(feature = "unity")]
    fn unity_plugin_unload() {}
}