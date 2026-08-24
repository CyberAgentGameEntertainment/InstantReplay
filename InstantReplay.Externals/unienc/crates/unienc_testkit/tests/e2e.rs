//! Desktop driver for the shared end-to-end harness.
//!
//! Whichever backend the target selects is the one under test: VideoToolbox on
//! Apple platforms, Media Foundation on Windows, FFmpeg elsewhere.

use std::path::PathBuf;

use unienc_testkit::{E2eConfig, e2e, mp4, verify};

/// Cargo hands integration tests a directory for their artifacts, which keeps
/// the muxed file out of the crate directory.
fn output_path(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_TARGET_TMPDIR")).join(name)
}

#[test]
fn encodes_and_muxes_a_playable_mp4() {
    let config = E2eConfig::default();
    let path = output_path("e2e.mp4");

    let report = e2e::run(&config, &path).expect("the encode failed");
    if let Err(error) = verify::verify_report(&report, &config) {
        panic!("the run did not do what was asked: {error}");
    }

    let bytes = std::fs::read(&path).expect("the muxed file is missing");
    let summary = mp4::summarize(&bytes).expect("the output is not a readable MP4");

    // Printed unconditionally: when this fails on a machine that is not to hand,
    // the log is the only evidence of what came out.
    println!("{}", verify::describe(&summary));

    if let Err(error) = verify::verify_mp4(&summary, &config) {
        panic!("{}\n{}", error, verify::describe(&summary));
    }
}
