//! Shared harness for exercising `unienc` end to end.
//!
//! The point of this crate is that one pipeline definition serves every target.
//! A build host runs it through `cargo test`; a device or a browser harness links
//! the same [`e2e::run`] and [`verify`] pair behind its own entry point. Whatever
//! drives it, the encode and the assertions are identical, so a backend cannot
//! pass on the desktop for reasons that do not hold on a phone.
//!
//! ```no_run
//! let config = unienc_testkit::E2eConfig::default();
//! let report = unienc_testkit::e2e::run(&config, std::path::Path::new("out.mp4")).unwrap();
//! unienc_testkit::verify::verify_report(&report, &config).unwrap();
//!
//! let bytes = std::fs::read("out.mp4").unwrap();
//! let summary = unienc_testkit::mp4::summarize(&bytes).unwrap();
//! unienc_testkit::verify::verify_mp4(&summary, &config).unwrap();
//! ```

pub mod e2e;
pub mod mp4;
pub mod options;
pub mod pattern;
pub mod runtime;
pub mod verify;

pub use e2e::{E2eConfig, E2eReport};
pub use runtime::TestRuntime;
