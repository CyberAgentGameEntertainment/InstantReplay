
#[cfg(target_vendor = "apple")]
pub type PlatformEncodingSystem<V, A> = unienc_apple_vt::VideoToolboxEncodingSystem<V, A>;

#[cfg(target_os = "android")]
pub type PlatformEncodingSystem<V, A> = unienc_android_mc::MediaCodecEncodingSystem<V, A>;

#[cfg(windows)]
pub type PlatformEncodingSystem<V, A> = unienc_windows_mf::MediaFoundationEncodingSystem<V, A>;

#[cfg(all(
    unix,
    not(any(target_vendor = "apple", target_os = "android", windows))
))]
pub type PlatformEncodingSystem<V, A> = unienc_ffmpeg::FFmpegEncodingSystem<V, A>;

#[cfg(not(any(target_vendor = "apple", target_os = "android", windows, unix)))]
pub type PlatformEncodingSystem<V, A> = ();

#[cfg(not(any(target_vendor = "apple", target_os = "android", windows, unix)))]
compile_error!("Unsupported platform");


