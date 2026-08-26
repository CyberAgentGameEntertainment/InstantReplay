use crate::e2e::E2eConfig;

/// Interval the backends applied before it became configurable, kept so that the harness exercises
/// the same key-frame spacing it always has.
const DEFAULT_IDR_INTERVAL_SECONDS: f32 = 1.0;

#[derive(Debug, Clone, Copy)]
pub struct TestVideoOptions {
    pub width: u32,
    pub height: u32,
    pub fps_hint: u32,
    pub bitrate: u32,
    pub idr_interval_seconds: f32,
}

impl From<&E2eConfig> for TestVideoOptions {
    fn from(config: &E2eConfig) -> Self {
        Self {
            width: config.width,
            height: config.height,
            fps_hint: config.fps,
            bitrate: config.video_bitrate,
            idr_interval_seconds: DEFAULT_IDR_INTERVAL_SECONDS,
        }
    }
}

impl unienc_common::VideoEncoderOptions for TestVideoOptions {
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

    fn idr_interval_seconds(&self) -> f32 {
        self.idr_interval_seconds
    }
}

#[derive(Debug, Clone, Copy)]
pub struct TestAudioOptions {
    pub sample_rate: u32,
    pub channels: u32,
    pub bitrate: u32,
}

impl From<&E2eConfig> for TestAudioOptions {
    fn from(config: &E2eConfig) -> Self {
        Self {
            sample_rate: config.sample_rate,
            channels: config.channels,
            bitrate: config.audio_bitrate,
        }
    }
}

impl unienc_common::AudioEncoderOptions for TestAudioOptions {
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
