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
