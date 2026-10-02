use std::ffi::c_void;
use std::fmt::Debug;
use std::future::Future;
use std::mem::size_of;
use std::path::Path;

use crate::buffer::SharedBuffer;
use bincode::{Decode, Encode};

pub mod buffer;
pub mod error;
mod runtime;
#[cfg(feature = "unity")]
pub mod unity;

pub use crate::runtime::*;
pub use error::{CategorizedError, CommonError, ErrorCategory, OptionExt, Result, ResultExt};

pub trait Encoder {
    type InputType: EncoderInput + 'static;
    type OutputType: EncoderOutput + 'static;
    fn get(self) -> Result<(Self::InputType, Self::OutputType)>;
}

pub trait CompletionHandle {
    fn finish(self) -> impl Future<Output = Result<()>> + Send;
}

pub trait Muxer: Send {
    type VideoInputType: MuxerInput + 'static;
    type AudioInputType: MuxerInput + 'static;
    type CompletionHandleType: CompletionHandle + 'static;

    fn get_inputs(
        self,
    ) -> Result<(
        Self::VideoInputType,
        Self::AudioInputType,
        Self::CompletionHandleType,
    )>;
}

pub trait MuxerInput: Send + 'static {
    type Data: Send;
    fn push(&mut self, data: Self::Data) -> impl Future<Output = Result<()>> + Send;
    fn finish(self) -> impl Future<Output = Result<()>> + Send;
}

pub trait EncodingSystem {
    type VideoEncoderOptionsType: VideoEncoderOptions;
    type AudioEncoderOptionsType: AudioEncoderOptions;
    type VideoEncoderType: Encoder<
        InputType: EncoderInput<Data = VideoSample<Self::BlitSourceType>>,
    >;
    type AudioEncoderType: Encoder<InputType: EncoderInput<Data = AudioSample>>;
    type MuxerType: Muxer<
            VideoInputType: MuxerInput<
                Data = <<Self::VideoEncoderType as Encoder>::OutputType as EncoderOutput>::Data,
            >,
            AudioInputType: MuxerInput<
                Data = <<Self::AudioEncoderType as Encoder>::OutputType as EncoderOutput>::Data,
            >,
        >;
    type BlitSourceType: TryFromUnityNativeTexturePointer + Send;
    type RuntimeType: Runtime;

    fn new(
        video_options: &Self::VideoEncoderOptionsType,
        audio_options: &Self::AudioEncoderOptionsType,
        runtime: Self::RuntimeType,
    ) -> Self;
    fn new_video_encoder(&self) -> Result<Self::VideoEncoderType>;
    fn new_audio_encoder(&self) -> Result<Self::AudioEncoderType>;
    fn new_muxer(&self, output_path: &Path) -> Result<Self::MuxerType>;

    fn is_blit_supported(&self) -> bool {
        false
    }
}

pub trait TryFromUnityNativeTexturePointer: Sized {
    fn try_from_unity_native_texture_ptr(ptr: *mut c_void) -> Result<Self>;
}

pub struct UnsupportedBlitData;

impl TryFromUnityNativeTexturePointer for UnsupportedBlitData {
    fn try_from_unity_native_texture_ptr(_ptr: *mut c_void) -> Result<Self> {
        Err(CommonError::BlitNotSupported)
    }
}

pub trait VideoEncoderOptions: Clone + Copy {
    fn width(&self) -> u32;
    fn height(&self) -> u32;
    fn fps_hint(&self) -> u32;
    fn bitrate(&self) -> u32;
}

pub trait AudioEncoderOptions: Clone + Copy {
    fn sample_rate(&self) -> u32;
    fn channels(&self) -> u32;
    fn bitrate(&self) -> u32;
}

// #[derive(Clone)]
pub struct VideoSample<BlitSourceType> {
    pub frame: VideoFrame<BlitSourceType>,
    pub timestamp: f64,
}

pub enum VideoFrame<BlitSourceType> {
    Bgra32(VideoFrameBgra32),
    BlitSource {
        texture_token: usize,
        width: u32,
        height: u32,
        graphics_format: u32,
        flip_vertically: bool,
        is_gamma_workflow: bool,
        event_issuer: Box<dyn GraphicsEventIssuer + Send>,
        _phantom: std::marker::PhantomData<BlitSourceType>,
    },
}

pub struct VideoFrameBgra32 {
    pub buffer: SharedBuffer,
    pub width: u32,
    pub height: u32,
}

impl VideoFrameBgra32 {
    pub fn to_yuv420_planes(
        &self,
        padded_size: Option<(u32, u32)>,
    ) -> Result<(Vec<u8>, Vec<u8>, Vec<u8>)> {
        let data = self.buffer.data();
        let w = padded_size.map_or(self.width, |(w, _)| w);
        let h = padded_size.map_or(self.height, |(_, h)| h);
        let w_half = (w + 1) >> 1;
        let h_half = (h + 1) >> 1;
        let padded_y_size = (w * h) as usize;
        let padded_uv_size = (w_half * h_half) as usize;

        // Create padded YUV data arrays
        let mut y_data = vec![16u8; padded_y_size]; // Black level for Y
        let mut u_data = vec![128u8; padded_uv_size]; // Neutral for U
        let mut v_data = vec![128u8; padded_uv_size]; // Neutral for V

        // Convert ARGB to YUV for the original image area only.
        //
        // BT.709 limited range ("studio swing"), 8-bit fixed point with a denominator of 256:
        //
        //     Y  = 16  + (219/255) * ( 0.2126 R + 0.7152 G + 0.0722 B )
        //     Cb = 128 + (224/255) * ( B - Y ) / 1.8556
        //     Cr = 128 + (224/255) * ( R - Y ) / 1.5748
        //
        // Scaling those factors by 256 gives (46.74, 157.24, 15.87) for Y, (-25.76, -86.67,
        // 112.43) for Cb and (112.43, -102.13, -10.30) for Cr. The Cb row is rounded to
        // (-26, -86, 112) instead of to the nearest integers so that every row sums to the value
        // the reference formula requires: 220 for Y, which maps white to 235 and black to 16, and
        // 0 for Cb and Cr, which maps neutral colors to exactly 128. That also keeps both chroma
        // rows inside the nominal 16..240 range at the primary extremes, as the BT.601
        // coefficients this replaces did. The results therefore always fit in u8 without clamping.
        //
        // The color tags written by each platform encoder must stay in sync with these
        // coefficients.
        for y in 0..self.height {
            for x in 0..self.width {
                let bgra_idx = ((y * self.width + x) * 4) as usize;
                let r = data[bgra_idx + 2] as i32;
                let g = data[bgra_idx + 1] as i32;
                let b = data[bgra_idx] as i32;

                let y_val = (((47 * r + 157 * g + 16 * b + 128) >> 8) + 16) as u8;

                let y_idx = (y * w + x) as usize;
                y_data[y_idx] = y_val;

                // Sample U and V for every 2x2 block (4:2:0 subsampling)
                if x % 2 == 0 && y % 2 == 0 {
                    let u_val = (((-26 * r - 86 * g + 112 * b + 128) >> 8) + 128) as u8;
                    let v_val = (((112 * r - 102 * g - 10 * b + 128) >> 8) + 128) as u8;

                    let uv_idx = ((y / 2) * (w / 2) + (x / 2)) as usize;
                    u_data[uv_idx] = u_val;
                    v_data[uv_idx] = v_val;
                }
            }
        }

        Ok((y_data, u_data, v_data))
    }
}

#[derive(Clone)]
pub struct AudioSample {
    pub data: Vec<i16>,
    pub timestamp_in_samples: u64,
}

impl AudioSample {
    /// Returns the sample data as signed 16-bit little-endian PCM bytes.
    pub fn data_as_s16le_bytes(&self) -> Vec<u8> {
        let mut bytes = Vec::with_capacity(self.data.len() * size_of::<i16>());
        for &sample in &self.data {
            bytes.extend_from_slice(&sample.to_le_bytes());
        }
        bytes
    }
}

/// Computes the forward discontinuity (in samples) between the expected next input position and the
/// actual timestamp of an incoming audio sample.
///
/// `expected_next` is the position the next input was expected at, i.e. the previous push's timestamp
/// plus the number of frames it delivered. Returns the number of samples by which the input timeline
/// jumped forward — to be reflected in the emitted PTS (Apple/Android) or filled with silence (FFmpeg)
/// so audio does not drift ahead of video. Returns 0 when the input is continuous, when this is the
/// first push (`expected_next` is `None`), or when the timestamp jumped backward (backward jumps are
/// ignored to keep PTS monotonic, which the muxers require).
pub fn forward_audio_discontinuity(expected_next: Option<u64>, actual_timestamp: u64) -> u64 {
    match expected_next {
        Some(expected) if actual_timestamp > expected => actual_timestamp - expected,
        _ => 0,
    }
}

pub trait EncodedData: Encode + Decode<()> {
    fn timestamp(&self) -> f64;
    fn set_timestamp(&mut self, timestamp: f64);
    fn kind(&self) -> UniencSampleKind;
}

#[repr(i8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UniencSampleKind {
    Interpolated = 0,
    Key = 1,
    Metadata = 2,
}

pub trait EncoderInput: Send + 'static {
    type Data: Send;
    fn push(&mut self, data: Self::Data) -> impl Future<Output = Result<()>> + Send;
}

pub trait GraphicsEventIssuer: Send + 'static {
    fn issue_graphics_event(
        &self,
        callback: Box<dyn FnOnce(*mut c_void) + Send + 'static>,
        event_id: i32,
        texture_token: usize,
    );
}

pub trait EncoderOutput: Send {
    type Data: EncodedData + Send;
    fn pull(&mut self) -> impl Future<Output = Result<Option<Self::Data>>> + Send;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn forward_audio_discontinuity_handles_continuity_gaps_and_backward_jumps() {
        // First push: no expectation yet, so no discontinuity.
        assert_eq!(forward_audio_discontinuity(None, 0), 0);
        assert_eq!(forward_audio_discontinuity(None, 48_000), 0);

        // Continuous input: actual timestamp matches the expected next position.
        assert_eq!(forward_audio_discontinuity(Some(48_000), 48_000), 0);

        // Forward discontinuity (dropped / paused audio): report the gap so it can be reflected in PTS.
        assert_eq!(forward_audio_discontinuity(Some(48_000), 72_000), 24_000);

        // Backward jump: ignored to keep PTS monotonic.
        assert_eq!(forward_audio_discontinuity(Some(48_000), 24_000), 0);
        assert_eq!(forward_audio_discontinuity(Some(48_000), 0), 0);
    }

    #[test]
    fn audio_sample_data_as_s16le_bytes_uses_little_endian_order() {
        let sample = AudioSample {
            data: vec![0x1234, -2, i16::MIN],
            timestamp_in_samples: 0,
        };

        assert_eq!(
            sample.data_as_s16le_bytes(),
            vec![0x34, 0x12, 0xfe, 0xff, 0x00, 0x80]
        );
    }

    /// A 2x2 frame of a single color, which converts to exactly one sample on every plane.
    fn solid_bgra_frame(r: u8, g: u8, b: u8) -> VideoFrameBgra32 {
        VideoFrameBgra32 {
            buffer: SharedBuffer::new_unmanaged([b, g, r, 255].repeat(4)),
            width: 2,
            height: 2,
        }
    }

    fn convert_solid(r: u8, g: u8, b: u8) -> (u8, u8, u8) {
        let (y, u, v) = solid_bgra_frame(r, g, b).to_yuv420_planes(None).unwrap();
        (y[0], u[0], v[0])
    }

    /// BT.709 limited range, evaluated in floating point straight from the definition.
    fn bt709_limited_reference(r: u8, g: u8, b: u8) -> (f64, f64, f64) {
        let (r, g, b) = (r as f64 / 255.0, g as f64 / 255.0, b as f64 / 255.0);
        let luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        (
            16.0 + 219.0 * luma,
            128.0 + 224.0 * (b - luma) / 1.8556,
            128.0 + 224.0 * (r - luma) / 1.5748,
        )
    }

    #[test]
    fn to_yuv420_planes_maps_white_and_black_to_the_limited_range_ends() {
        assert_eq!(convert_solid(255, 255, 255), (235, 128, 128));
        assert_eq!(convert_solid(0, 0, 0), (16, 128, 128));
    }

    #[test]
    fn to_yuv420_planes_keeps_grays_neutral() {
        for level in 0..=255u8 {
            let (_, u, v) = convert_solid(level, level, level);
            assert_eq!((u, v), (128, 128), "gray level {level}");
        }
    }

    #[test]
    fn to_yuv420_planes_uses_bt709_rather_than_bt601() {
        // The primaries are where the two matrices differ most. Pure red is Y=63 under BT.709 and
        // Y=81 under BT.601, so this fails clearly if the BT.601 coefficients come back.
        assert_eq!(convert_solid(255, 0, 0), (63, 102, 240));
        assert_eq!(convert_solid(0, 255, 0), (172, 42, 26));
        assert_eq!(convert_solid(0, 0, 255), (32, 240, 118));
    }

    #[test]
    fn to_yuv420_planes_matches_the_bt709_definition_within_rounding() {
        // The fixed-point coefficients are rounded to 1/256, and the Cb row is deliberately rounded
        // away from the nearest integers to keep neutral colors at exactly 128 (see
        // `to_yuv420_planes`). That costs up to about 1.15 steps on Cb for saturated greens. The
        // BT.601 coefficients this replaced are off by up to 28 steps on Y, so the tolerance
        // still tells the two matrices apart by a wide margin.
        for r in (0..=255u8).step_by(5) {
            for g in (0..=255u8).step_by(5) {
                for b in (0..=255u8).step_by(5) {
                    let actual = convert_solid(r, g, b);
                    let expected = bt709_limited_reference(r, g, b);
                    for (component, actual, expected) in [
                        ("Y", actual.0, expected.0),
                        ("Cb", actual.1, expected.1),
                        ("Cr", actual.2, expected.2),
                    ] {
                        assert!(
                            (actual as f64 - expected).abs() <= 1.5,
                            "{component} of rgb({r}, {g}, {b}) is {actual}, expected {expected:.2}"
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn to_yuv420_planes_fills_padding_with_black() {
        let frame = solid_bgra_frame(255, 255, 255);
        let (y, u, v) = frame.to_yuv420_planes(Some((4, 4))).unwrap();

        assert_eq!(y.len(), 16);
        assert_eq!((u.len(), v.len()), (4, 4));
        for row in 0..4 {
            for col in 0..4 {
                let expected = if row < 2 && col < 2 { 235 } else { 16 };
                assert_eq!(y[row * 4 + col], expected, "luma at ({col}, {row})");
            }
        }
        assert!(u.iter().chain(&v).all(|&c| c == 128));
    }
}
