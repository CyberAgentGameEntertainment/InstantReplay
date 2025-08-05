// MediaCodec and MediaFormat configuration constants

/// Video MIME types
pub const MIME_TYPE_VIDEO_AVC: &str = "video/avc"; // H.264
pub const MIME_TYPE_VIDEO_HEVC: &str = "video/hevc"; // H.265

/// Audio MIME types
pub const MIME_TYPE_AUDIO_AAC: &str = "audio/mp4a-latm"; // AAC
pub const MIME_TYPE_AUDIO_OPUS: &str = "audio/opus";

/// MediaFormat keys
pub mod format_keys {
    // Common keys
    pub const KEY_MIME: &str = "mime";
    pub const KEY_BITRATE: &str = "bitrate";
    pub const KEY_DURATION: &str = "durationUs";
    pub const KEY_MAX_INPUT_SIZE: &str = "max-input-size";
    
    // Video keys
    pub const KEY_WIDTH: &str = "width";
    pub const KEY_HEIGHT: &str = "height";
    pub const KEY_FRAME_RATE: &str = "frame-rate";
    pub const KEY_COLOR_FORMAT: &str = "color-format";
    pub const KEY_I_FRAME_INTERVAL: &str = "i-frame-interval";
    pub const KEY_PROFILE: &str = "profile";
    pub const KEY_LEVEL: &str = "level";
    
    // Audio keys
    pub const KEY_SAMPLE_RATE: &str = "sample-rate";
    pub const KEY_CHANNEL_COUNT: &str = "channel-count";
    pub const KEY_AAC_PROFILE: &str = "aac-profile";
}

/// Color formats
pub mod color_formats {
    use jni::sys::jint;
    
    pub const COLOR_FORMAT_YUV420_FLEXIBLE: jint = 0x7F420888;
    pub const COLOR_FORMAT_YUV420_PLANAR: jint = 19;
    pub const COLOR_FORMAT_YUV420_SEMI_PLANAR: jint = 21;
    pub const COLOR_FORMAT_SURFACE: jint = 0x7F000789;
}

/// H.264 profiles
pub mod avc_profiles {
    use jni::sys::jint;
    
    pub const PROFILE_BASELINE: jint = 1;
    pub const PROFILE_MAIN: jint = 2;
    pub const PROFILE_HIGH: jint = 8;
}

/// H.264 levels
pub mod avc_levels {
    use jni::sys::jint;
    
    pub const LEVEL_1: jint = 1;
    pub const LEVEL_2: jint = 8;
    pub const LEVEL_3: jint = 32;
    pub const LEVEL_31: jint = 64;
    pub const LEVEL_4: jint = 128;
    pub const LEVEL_41: jint = 256;
    pub const LEVEL_5: jint = 512;
    pub const LEVEL_51: jint = 1024;
}

/// AAC profiles
pub mod aac_profiles {
    use jni::sys::jint;
    
    pub const AAC_OBJECT_TYPE_AAC_LC: jint = 2;
    pub const AAC_OBJECT_TYPE_AAC_HE: jint = 5;
    pub const AAC_OBJECT_TYPE_AAC_HEV2: jint = 29;
}

/// Default encoder settings
pub mod defaults {
    pub const VIDEO_BITRATE: u32 = 8_000_000; // 8 Mbps
    pub const VIDEO_FPS: u32 = 30;
    pub const VIDEO_I_FRAME_INTERVAL: i32 = 1; // 1 second
    
    pub const AUDIO_BITRATE: u32 = 128_000; // 128 kbps
    pub const AUDIO_SAMPLE_RATE: u32 = 44100;
    pub const AUDIO_CHANNELS: u32 = 2;
}

/// MediaMuxer output formats
pub mod muxer_formats {
    use jni::sys::jint;
    
    pub const OUTPUT_FORMAT_MPEG_4: jint = 0;
    pub const OUTPUT_FORMAT_WEBM: jint = 1;
    pub const OUTPUT_FORMAT_3GPP: jint = 2;
}