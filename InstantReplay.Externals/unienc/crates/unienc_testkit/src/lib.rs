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
//! match unienc_testkit::run_and_verify(&config, std::path::Path::new("out.mp4")) {
//!     Ok(description) => println!("{description}"),
//!     Err(message) => panic!("{message}"),
//! }
//! ```

pub mod driver;
pub mod e2e;
pub mod mp4;
pub mod options;
pub mod pattern;
pub mod runtime;
pub mod verify;
#[cfg(target_os = "emscripten")]
pub mod web;

#[cfg(not(target_os = "emscripten"))]
pub use driver::run_and_verify;
pub use driver::verify_output;
pub use e2e::{E2eConfig, E2eReport};
pub use runtime::TestRuntime;
