use jni::sys::jint;

/// Video MIME types
pub const MIME_TYPE_VIDEO_AVC: &str = "video/avc"; // H.264

/// Audio MIME types
pub const MIME_TYPE_AUDIO_AAC: &str = "audio/mp4a-latm"; // AAC

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
    pub const KEY_PRIORITY: &str = "priority";
    pub const KEY_OPERATING_RATE: &str = "operating-rate";
    pub const KEY_COLOR_STANDARD: &str = "color-standard";
    pub const KEY_COLOR_TRANSFER: &str = "color-transfer";
    pub const KEY_COLOR_RANGE: &str = "color-range";

    // Audio keys
    pub const KEY_SAMPLE_RATE: &str = "sample-rate";
    pub const KEY_CHANNEL_COUNT: &str = "channel-count";
    pub const KEY_AAC_PROFILE: &str = "aac-profile";
}

pub const COLOR_FORMAT_SURFACE: jint = 0x7F000789;
pub const COLOR_FORMAT_YUV420_FLEXIBLE: jint = 0x7F420888;

/// `MediaFormat.COLOR_STANDARD_BT709`, `COLOR_TRANSFER_SDR_VIDEO` and `COLOR_RANGE_LIMITED`.
/// All three were introduced in API 24, below the minimum this crate targets, so setting them
/// needs no version guard.
pub const COLOR_STANDARD_BT709: jint = 1;
pub const COLOR_TRANSFER_SDR_VIDEO: jint = 3;
pub const COLOR_RANGE_LIMITED: jint = 2;
pub const AAC_OBJECT_TYPE_AAC_LC: jint = 2;

pub const MUXER_OUTPUT_FORMAT_MPEG_4: jint = 0;
