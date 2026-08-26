//! Assertions on what the pipeline actually produced.
//!
//! Every check here exists because something once went wrong without the test
//! noticing. A run that only asserts "no error was returned" passes while the
//! output is missing its tail, has the wrong frame rate, or carries no decoder
//! configuration at all.
//!
//! Platforms disagree in the details, so the tolerances are deliberate rather
//! than incidental; each one says what it is absorbing.

use std::fmt;

use crate::e2e::{E2eConfig, E2eReport};
use crate::mp4::{Mp4Summary, Track, TrackKind};

/// An AAC frame always covers 1024 samples per channel.
const AAC_SAMPLES_PER_FRAME: u64 = 1024;

/// Encoders prepend priming samples and pad the final frame, and how many they
/// use is their own business: VideoToolbox and FFmpeg differ by a few frames for
/// identical input. Only a gross mismatch is a defect.
const AUDIO_FRAME_SLACK: u64 = 32;

/// Track durations are derived from sample deltas in an integer timescale, so
/// they land near rather than on the nominal length.
const DURATION_TOLERANCE_SECS: f64 = 0.25;

/// How far apart the tracks may start before playback is audibly out of sync.
/// Encoder priming shifts one track by a few milliseconds at most.
const SYNC_TOLERANCE_SECS: f64 = 0.05;

/// Everything that did not hold.
#[derive(Debug)]
pub struct VerifyError {
    pub failures: Vec<String>,
}

impl fmt::Display for VerifyError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        writeln!(f, "{} check(s) failed:", self.failures.len())?;
        for failure in &self.failures {
            writeln!(f, "  - {failure}")?;
        }
        Ok(())
    }
}

impl std::error::Error for VerifyError {}

#[derive(Default)]
struct Findings {
    failures: Vec<String>,
}

impl Findings {
    fn check(&mut self, holds: bool, describe: impl FnOnce() -> String) {
        if !holds {
            self.failures.push(describe());
        }
    }

    fn into_result(self) -> Result<(), VerifyError> {
        if self.failures.is_empty() {
            Ok(())
        } else {
            Err(VerifyError {
                failures: self.failures,
            })
        }
    }
}

/// Checks the report a run produced against what was asked of it.
pub fn verify_report(report: &E2eReport, config: &E2eConfig) -> Result<(), VerifyError> {
    let mut findings = Findings::default();

    findings.check(report.video_frames_pushed == config.video_frames(), || {
        format!(
            "pushed {} video frames, expected {}",
            report.video_frames_pushed,
            config.video_frames()
        )
    });
    findings.check(report.audio_chunks_pushed == config.duration_secs, || {
        format!(
            "pushed {} audio chunks, expected {}",
            report.audio_chunks_pushed, config.duration_secs
        )
    });
    // The encoder emits at least one item per frame, plus parameter sets on the
    // platforms that deliver them out of band.
    findings.check(report.video_data_pulled >= config.video_frames(), || {
        format!(
            "pulled only {} encoded video items for {} frames",
            report.video_data_pulled,
            config.video_frames()
        )
    });
    findings.check(report.audio_data_pulled > 0, || {
        "pulled no encoded audio at all".to_string()
    });

    findings.into_result()
}

/// Checks the muxed file against what was asked of the run.
pub fn verify_mp4(summary: &Mp4Summary, config: &E2eConfig) -> Result<(), VerifyError> {
    let mut findings = Findings::default();

    let expected_duration = config.duration_secs as f64;

    match summary.track(TrackKind::Video) {
        None => findings.failures.push("no video track".to_string()),
        Some(track) => verify_video_track(&mut findings, track, config, expected_duration),
    }

    match summary.track(TrackKind::Audio) {
        None => findings.failures.push("no audio track".to_string()),
        Some(track) => verify_audio_track(&mut findings, track, config, expected_duration),
    }

    findings.check(summary.tracks.len() == 2, || {
        format!(
            "expected exactly a video and an audio track, found {:?}",
            summary
                .tracks
                .iter()
                .map(|track| (track.kind, track.format.clone()))
                .collect::<Vec<_>>()
        )
    });

    // A track pushed forward by an empty edit plays late. This is what a muxer
    // shifting one stream's timeline and not the other's looks like, and it is
    // silent in every other measurement: the tracks keep their own durations and
    // only the container grows.
    for track in &summary.tracks {
        findings.check(track.start_time <= SYNC_TOLERANCE_SECS, || {
            format!(
                "the {:?} track starts {:.3} s late, so it is out of sync",
                track.kind, track.start_time
            )
        });
    }

    // The container spans every track including those delays, so it catches the
    // same defect from the other side.
    findings.check(
        (summary.duration - expected_duration).abs() <= DURATION_TOLERANCE_SECS,
        || {
            format!(
                "the file is {:.3} s long, expected {:.3} s",
                summary.duration, expected_duration
            )
        },
    );

    findings.into_result()
}

fn verify_video_track(
    findings: &mut Findings,
    track: &Track,
    config: &E2eConfig,
    expected_duration: f64,
) {
    findings.check(track.format.starts_with("avc"), || {
        format!("video format is '{}', expected H.264", track.format)
    });
    findings.check(track.has_decoder_config, || {
        "video track carries no avcC decoder configuration, so it cannot be decoded".to_string()
    });
    findings.check(
        track.width == config.width && track.height == config.height,
        || {
            format!(
                "video is {}x{}, expected {}x{}",
                track.width, track.height, config.width, config.height
            )
        },
    );

    // Every frame pushed has to come out the other end. This is what a dropped
    // tail looks like, and it is the assertion the harness lacked when the CFR
    // handling and the muxer were each losing frames off the end.
    findings.check(track.sample_count == config.video_frames(), || {
        format!(
            "video has {} frames, expected {}",
            track.sample_count,
            config.video_frames()
        )
    });

    // A muxer that derives sample durations from the gaps between presentation
    // timestamps has nothing to derive the last one from. MediaMuxer gives it
    // zero, so the track legitimately ends a frame short, while AVFoundation and
    // FFmpeg give it a full interval. The frame count above is the strict check;
    // this one is about the timeline being the right length.
    let interval = 1.0 / config.fps as f64;
    let shortest = expected_duration - interval - DURATION_TOLERANCE_SECS;
    let longest = expected_duration + DURATION_TOLERANCE_SECS;
    findings.check((shortest..=longest).contains(&track.duration), || {
        format!(
            "video track is {:.3} s, expected between {:.3} s and {:.3} s",
            track.duration, shortest, longest
        )
    });

    // A decoder joining at the start needs the first sample to be a keyframe.
    findings.check(track.is_sync_sample(1), || {
        "the first video sample is not a sync sample".to_string()
    });

    let times = track.sample_times();
    let out_of_order = times.windows(2).position(|pair| pair[1] <= pair[0]);
    findings.check(out_of_order.is_none(), || {
        let at = out_of_order.unwrap();
        format!(
            "video sample times are not increasing at sample {}: {:?}",
            at + 1,
            &times[at..(at + 2).min(times.len())]
        )
    });

    // Raw H.264 has no timestamps of its own, so a wrong frame rate here means
    // the muxer was told the wrong one rather than that a frame went missing.
    let bad_interval = times
        .windows(2)
        .map(|pair| pair[1] - pair[0])
        .position(|delta| delta < interval * 0.5 || delta > interval * 2.0);
    findings.check(bad_interval.is_none(), || {
        let at = bad_interval.unwrap();
        format!(
            "video sample interval at sample {} is {:.4} s, expected about {:.4} s",
            at + 1,
            times[at + 1] - times[at],
            interval
        )
    });
}

fn verify_audio_track(
    findings: &mut Findings,
    track: &Track,
    config: &E2eConfig,
    expected_duration: f64,
) {
    findings.check(track.format == "mp4a", || {
        format!("audio format is '{}', expected 'mp4a'", track.format)
    });
    findings.check(track.has_decoder_config, || {
        "audio track carries no esds decoder configuration, so it cannot be decoded".to_string()
    });
    findings.check(track.sample_rate == config.sample_rate, || {
        format!(
            "audio is at {} Hz, expected {} Hz",
            track.sample_rate, config.sample_rate
        )
    });
    findings.check(track.channels == config.channels, || {
        format!(
            "audio has {} channels, expected {}",
            track.channels, config.channels
        )
    });

    let total_samples = config.duration_secs as u64 * config.sample_rate as u64;
    let least = total_samples.div_ceil(AAC_SAMPLES_PER_FRAME);
    let most = least + AUDIO_FRAME_SLACK;
    findings.check(
        (least..=most).contains(&(track.sample_count as u64)),
        || {
            format!(
                "audio has {} frames, expected between {} and {}",
                track.sample_count, least, most
            )
        },
    );

    findings.check(
        (track.duration - expected_duration).abs() <= DURATION_TOLERANCE_SECS,
        || {
            format!(
                "audio track is {:.3} s, expected {:.3} s",
                track.duration, expected_duration
            )
        },
    );
}

/// Formats a summary as a few lines, for a driver to print alongside a failure.
pub fn describe(summary: &Mp4Summary) -> String {
    let mut out = format!(
        "movie: timescale {}, duration {:.3} s, {} track(s)\n",
        summary.timescale,
        summary.duration,
        summary.tracks.len()
    );
    for track in &summary.tracks {
        out.push_str(&match track.kind {
            TrackKind::Video => format!(
                "  video: {} {}x{} ({}), {} frames, start {:.3} s, {:.3} s\n",
                track.format,
                track.width,
                track.height,
                match track.avc_profile {
                    Some(profile) => profile.to_string(),
                    None => "no decoder config".to_string(),
                },
                track.sample_count,
                track.start_time,
                track.duration,
            ),
            TrackKind::Audio => format!(
                "  audio: {} {} Hz {} ch, {} frames, start {:.3} s, {:.3} s, decoder config: {}\n",
                track.format,
                track.sample_rate,
                track.channels,
                track.sample_count,
                track.start_time,
                track.duration,
                track.has_decoder_config
            ),
            TrackKind::Other => format!("  other: {}\n", track.format),
        });
    }
    out
}
