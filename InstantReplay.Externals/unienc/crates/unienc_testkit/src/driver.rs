//! The one call a driver makes.
//!
//! Every target that runs this harness — `cargo test` on a build host, a JVM
//! shim on an Android device, a browser page — needs the same three steps in the
//! same order: encode, check the run, check the file. Putting them here rather
//! than in each driver is what keeps a platform from quietly skipping one.
//!
//! The result is a string either way, because a device harness usually has
//! nothing but a log to report through.

use std::path::Path;

// `run_and_verify` is the only user, and it is absent on the web.
#[cfg(not(target_os = "emscripten"))]
use crate::e2e;
use crate::e2e::{E2eConfig, E2eReport};
use crate::{mp4, verify};

/// Encodes to `output_path`, then verifies the run and the file it produced.
///
/// On success the description of the output is returned, so a driver can log
/// what it got rather than only that it was happy. On failure the message says
/// what did not hold, with the description appended when the file was readable.
#[cfg(not(target_os = "emscripten"))]
pub fn run_and_verify(config: &E2eConfig, output_path: &Path) -> Result<String, String> {
    let report =
        e2e::run(config, output_path).map_err(|error| format!("the encode failed: {error}"))?;
    verify_output(config, output_path, &report)
}

/// The checks that follow a run.
///
/// Split from the run itself for the sake of a driver that cannot block, which
/// has to do these when its future resolves rather than after a blocking call.
pub fn verify_output(
    config: &E2eConfig,
    output_path: &Path,
    report: &E2eReport,
) -> Result<String, String> {
    verify::verify_report(report, config)
        .map_err(|error| format!("the run did not do what was asked: {error}"))?;

    let bytes = std::fs::read(output_path)
        .map_err(|error| format!("cannot read {}: {error}", output_path.display()))?;
    let summary = mp4::summarize(&bytes)
        .map_err(|error| format!("the output is not a readable MP4: {error}"))?;

    let described = verify::describe(&summary);
    verify::verify_mp4(&summary, config).map_err(|error| format!("{error}{described}"))?;

    Ok(described)
}
