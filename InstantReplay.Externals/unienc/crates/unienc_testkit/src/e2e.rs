//! The end-to-end encode, shared by every driver.
//!
//! Keeping the pipeline here rather than in a `tests/` file is what lets the
//! same run be driven from a `cargo test` on a build host, from a C entry point
//! linked into a device harness, or from an Emscripten `main` in a browser,
//! without the platforms drifting apart.

use std::path::Path;

use futures::channel::oneshot::Canceled;
use unienc_common::{
    AudioSample, CommonError, CompletionHandle, EncodedData, Encoder, EncoderInput, EncoderOutput,
    EncodingSystem, Muxer, MuxerInput, ResultExt, VideoFrame, VideoFrameBgra32, VideoSample,
    buffer::SharedBuffer,
};

use crate::options::{TestAudioOptions, TestVideoOptions};
use crate::pattern;
use crate::runtime::TestRuntime;

/// The encoded video type a given encoding system produces.
pub type VideoData<S> =
    <<<S as EncodingSystem>::VideoEncoderType as Encoder>::OutputType as EncoderOutput>::Data;
/// The encoded audio type a given encoding system produces.
pub type AudioData<S> =
    <<<S as EncodingSystem>::AudioEncoderType as Encoder>::OutputType as EncoderOutput>::Data;

/// What to encode.
#[derive(Debug, Clone, Copy)]
pub struct E2eConfig {
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub video_bitrate: u32,
    pub sample_rate: u32,
    pub channels: u32,
    pub audio_bitrate: u32,
    /// Length of the encoded material in seconds.
    pub duration_secs: u32,
    /// Added to every input timestamp, then subtracted again before muxing.
    ///
    /// Capture timestamps in a player are wall-clock-ish rather than starting at
    /// zero, so a pipeline that quietly assumes a zero-based timeline has to
    /// fail here.
    pub timestamp_offset: f64,
}

impl Default for E2eConfig {
    fn default() -> Self {
        Self {
            width: 1280,
            height: 720,
            fps: 1,
            video_bitrate: 1_000_000,
            sample_rate: 48000,
            channels: 2,
            audio_bitrate: 128_000,
            duration_secs: 10,
            timestamp_offset: 100.0,
        }
    }
}

impl E2eConfig {
    /// Number of frames the run pushes into the video encoder.
    pub fn video_frames(&self) -> u32 {
        self.duration_secs * self.fps
    }
}

/// What a run actually did, so a driver can assert on it without reopening the
/// output.
#[derive(Debug, Clone, Default)]
pub struct E2eReport {
    pub video_frames_pushed: u32,
    pub audio_chunks_pushed: u32,
    /// Encoded video items pulled out of the encoder, parameter sets included.
    pub video_data_pulled: u32,
    pub audio_data_pulled: u32,
}

/// Runs the whole pipeline on this platform's encoding system and writes an MP4
/// to `output_path`.
pub fn run(config: &E2eConfig, output_path: &Path) -> unienc_common::Result<E2eReport> {
    let runtime = TestRuntime::new();
    let encoding_system = unienc::PlatformEncodingSystem::new(
        &TestVideoOptions::from(config),
        &TestAudioOptions::from(config),
        runtime.clone(),
    );

    futures::executor::block_on(run_with(encoding_system, runtime, config, output_path))
}

/// Runs the pipeline on a specific encoding system.
///
/// Generic over the system so that a driver can exercise one backend directly
/// instead of whatever the target selects.
pub async fn run_with<S>(
    encoding_system: S,
    runtime: TestRuntime,
    config: &E2eConfig,
    output_path: &Path,
) -> unienc_common::Result<E2eReport>
where
    S: EncodingSystem<RuntimeType = TestRuntime> + Send,
    VideoData<S>: bincode::Encode + bincode::Decode<()>,
    AudioData<S>: bincode::Encode + bincode::Decode<()>,
{
    let video_encoder = encoding_system.new_video_encoder()?;
    let audio_encoder = encoding_system.new_audio_encoder()?;
    let muxer = encoding_system.new_muxer(output_path)?;

    let (mut video_input, mut video_output) = video_encoder.get()?;
    let (mut audio_input, mut audio_output) = audio_encoder.get()?;
    let (mut mux_video, mut mux_audio, completion) = muxer.get_inputs()?;

    let config = *config;

    let emit_video = runtime.spawn_with_result(async move {
        for index in 0..config.video_frames() {
            let data = pattern::video_frame_bgra32(config.width, config.height, index);
            video_input
                .push(VideoSample {
                    frame: VideoFrame::Bgra32(VideoFrameBgra32 {
                        buffer: SharedBuffer::new_unmanaged(data),
                        width: config.width,
                        height: config.height,
                    }),
                    timestamp: index as f64 / config.fps as f64 + config.timestamp_offset,
                })
                .await?;
        }
        Ok(config.video_frames())
    });

    let emit_audio = runtime.spawn_with_result(async move {
        for second in 0..config.duration_secs as u64 {
            audio_input
                .push(AudioSample {
                    data: pattern::audio_samples_s16(config.sample_rate, config.channels, second),
                    timestamp_in_samples: second * config.sample_rate as u64,
                })
                .await?;
        }
        Ok(config.duration_secs)
    });

    let offset = config.timestamp_offset;
    let transfer_video = runtime.spawn_with_result(async move {
        let mut pulled = 0;
        while let Some(data) = video_output.pull().await? {
            // Round-tripping through bincode is how encoded data crosses the FFI
            // boundary in production, so the run exercises it too.
            let mut data = reencode(data)?;
            data.set_timestamp(data.timestamp() - offset);
            mux_video.push(data).await?;
            pulled += 1;
        }
        mux_video.finish().await?;
        Ok(pulled)
    });

    let transfer_audio = runtime.spawn_with_result(async move {
        let mut pulled = 0;
        while let Some(data) = audio_output.pull().await? {
            let data = reencode(data)?;
            mux_audio.push(data).await?;
            pulled += 1;
        }
        mux_audio.finish().await?;
        Ok(pulled)
    });

    // The inputs have to be finished before the muxer is waited on, otherwise
    // FFmpeg and MediaMuxer never see end of stream.
    let video_frames_pushed = join(emit_video).await?;
    let audio_chunks_pushed = join(emit_audio).await?;
    let video_data_pulled = join(transfer_video).await?;
    let audio_data_pulled = join(transfer_audio).await?;

    completion.finish().await?;

    Ok(E2eReport {
        video_frames_pushed,
        audio_chunks_pushed,
        video_data_pulled,
        audio_data_pulled,
    })
}

/// Awaits a spawned task, reporting a dropped executor as an error rather than
/// panicking so that the run's own failure is what surfaces.
async fn join<T>(
    handle: impl Future<Output = std::result::Result<unienc_common::Result<T>, Canceled>>,
) -> unienc_common::Result<T> {
    handle
        .await
        .map_err(|_| CommonError::Other("a pipeline task was cancelled".into()))?
}

/// Serialises encoded data and reads it straight back, the way the C layer moves
/// it between the encoder and the muxer.
fn reencode<T: bincode::Encode + bincode::Decode<()>>(data: T) -> unienc_common::Result<T> {
    let bytes = bincode::encode_to_vec(data, bincode::config::standard())
        .context("failed to encode sample for transfer")?;
    let (decoded, _) = bincode::decode_from_slice(&bytes, bincode::config::standard())
        .context("failed to decode sample after transfer")?;
    Ok(decoded)
}
