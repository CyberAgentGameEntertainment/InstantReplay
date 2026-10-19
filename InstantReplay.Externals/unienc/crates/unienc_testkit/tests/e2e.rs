//! Desktop and simulator driver for the shared end-to-end harness.
//!
//! Whichever backend the target selects is the one under test: VideoToolbox on
//! Apple platforms, Media Foundation on Windows, FFmpeg elsewhere. With a
//! simulator runner configured in `.cargo/config.toml`, the very same test runs
//! inside the iOS simulator.

use std::path::PathBuf;

use unienc_testkit::E2eConfig;

/// Cargo hands integration tests a directory for their artifacts, which keeps
/// the muxed file out of the crate directory.
fn output_path(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_TARGET_TMPDIR")).join(name)
}

fn run_and_verify(config: &E2eConfig, name: &str) {
    match unienc_testkit::run_and_verify(config, &output_path(&format!("{name}.mp4"))) {
        // Printed unconditionally: when this fails on a machine that is not to
        // hand, the log is the only evidence of what came out.
        Ok(description) => println!("{description}"),
        Err(message) => panic!("{message}"),
    }
}

#[test]
fn encodes_and_muxes_a_playable_mp4() {
    run_and_verify(&E2eConfig::default(), "e2e");
}

#[test]
fn honours_a_custom_idr_interval() {
    run_and_verify(&E2eConfig::custom_idr_interval(), "e2e-idr-interval");
}
