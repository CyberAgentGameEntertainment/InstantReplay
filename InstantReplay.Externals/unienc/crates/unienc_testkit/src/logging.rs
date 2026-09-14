//! Makes the backends' `log` records visible to whatever is driving the harness.
//!
//! The encoder crates report through the `log` facade, and the logger that the
//! Unity plugin installs lives in `unienc_c`. CI links this crate against a
//! backend directly and never links `unienc_c` at all, so without a logger here
//! every record a backend emits during a test is discarded — which is precisely
//! the output a failing run needs.
//!
//! Records go through [`crate::progress`] rather than `println!` so that they
//! interleave with the harness's own progress lines and are flushed one line at
//! a time. A device harness runs behind a pipe and may hang; unflushed
//! diagnostics from the run that hangs never arrive.

use std::sync::Once;

struct HarnessLogger;

impl log::Log for HarnessLogger {
    fn enabled(&self, metadata: &log::Metadata<'_>) -> bool {
        metadata.level() <= log::max_level()
    }

    fn log(&self, record: &log::Record<'_>) {
        if !self.enabled(record.metadata()) {
            return;
        }
        crate::progress::report(std::format_args!(
            "[{}] {}: {}",
            record.level(),
            record.target(),
            record.args()
        ));
    }

    fn flush(&self) {}
}

static LOGGER: HarnessLogger = HarnessLogger;
static INSTALL: Once = Once::new();

/// Installs the harness logger once per process.
///
/// The threshold is `Debug`: `Trace` carries per-frame dumps that would bury a
/// CI log, while `Debug` and above is what says which encoder was selected and
/// how it failed. Nothing here overrides a logger a driver installed first —
/// `set_logger` failing is not an error worth reporting from a test harness.
pub fn install() {
    INSTALL.call_once(|| {
        if log::set_logger(&LOGGER).is_ok() {
            log::set_max_level(log::LevelFilter::Debug);
        }
    });
}
