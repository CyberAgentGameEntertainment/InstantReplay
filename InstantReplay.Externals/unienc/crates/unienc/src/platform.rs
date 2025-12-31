
#[cfg(target_vendor = "apple")]
pub type PlatformEncodingSystem<V, A, R> = unienc_apple_vt::VideoToolboxEncodingSystem<V, A>;

#[cfg(target_os = "android")]
pub type PlatformEncodingSystem<V, A, R> = unienc_android_mc::MediaCodecEncodingSystem<V, A>;

#[cfg(windows)]
pub type PlatformEncodingSystem<V, A, R> = unienc_windows_mf::MediaFoundationEncodingSystem<V, A, R>;

#[cfg(target_arch = "wasm32")]
pub type PlatformEncodingSystem<V, A, R> = unienc_webcodecs::WebCodecsEncodingSystem<V, A, R>;

#[cfg(all(unix, not(any(target_vendor = "apple", target_os = "android", windows, target_arch = "wasm32"))))]
pub type PlatformEncodingSystem<V, A, R> = unienc_ffmpeg::FFmpegEncodingSystem<V, A>;

#[cfg(not(any(target_vendor = "apple", target_os = "android", windows, unix, target_arch = "wasm32")))]
pub type PlatformEncodingSystem<V, A, R> = ();

#[cfg(not(any(target_vendor = "apple", target_os = "android", windows, unix, target_arch = "wasm32")))]
compile_error!("Unsupported platform");


