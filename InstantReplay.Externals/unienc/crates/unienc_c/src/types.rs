use unienc::{AudioEncoderOptions, UniencSampleKind, VideoEncoderOptions};

#[repr(C)]
pub struct UniencSampleData {
    pub(crate) data: *const u8,
    pub(crate) size: usize,
    pub(crate) timestamp: f64,
    pub(crate) kind: UniencSampleKind,
}

impl Default for UniencSampleData {
    fn default() -> Self {
        Self {
            data: std::ptr::null(),
            size: 0,
            timestamp: 0.0,
            kind: UniencSampleKind::Interpolated,
        }
    }
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct VideoEncoderOptionsNative {
    pub width: u32,
    pub height: u32,
    pub fps_hint: u32,
    pub bitrate: u32,
    /// Maximum interval between IDR (key) frames, in seconds. Zero or negative means "leave the
    /// interval at the platform encoder's default"; `Option<f32>` has no stable `repr(C)` layout,
    /// so the absent case is carried by the sentinel instead of a separate discriminant field.
    pub idr_interval_seconds: f32,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct AudioEncoderOptionsNative {
    pub sample_rate: u32,
    pub channels: u32,
    pub bitrate: u32,
}

impl VideoEncoderOptions for VideoEncoderOptionsNative {
    fn width(&self) -> u32 {
        self.width
    }

    fn height(&self) -> u32 {
        self.height
    }

    fn fps_hint(&self) -> u32 {
        self.fps_hint
    }

    fn bitrate(&self) -> u32 {
        self.bitrate
    }

    fn idr_interval_seconds(&self) -> Option<f32> {
        (self.idr_interval_seconds > 0.0 && self.idr_interval_seconds.is_finite())
            .then_some(self.idr_interval_seconds)
    }
}

impl AudioEncoderOptions for AudioEncoderOptionsNative {
    fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    fn channels(&self) -> u32 {
        self.channels
    }

    fn bitrate(&self) -> u32 {
        self.bitrate
    }
}
