use crate::logging;
use log::LevelFilter;

/// Severity threshold for native logging. Mirrors `log::LevelFilter`.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum UniencLogLevel {
    Off = 0,
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
    Trace = 5,
}

impl From<UniencLogLevel> for LevelFilter {
    fn from(level: UniencLogLevel) -> Self {
        match level {
            UniencLogLevel::Off => LevelFilter::Off,
            UniencLogLevel::Error => LevelFilter::Error,
            UniencLogLevel::Warn => LevelFilter::Warn,
            UniencLogLevel::Info => LevelFilter::Info,
            UniencLogLevel::Debug => LevelFilter::Debug,
            UniencLogLevel::Trace => LevelFilter::Trace,
        }
    }
}

/// Installs the native logger if it is not installed yet, and discards any record below `level`.
/// Safe to call at any point; records emitted before the first call use the build-dependent default
/// (`Debug` for debug builds, `Info` for release builds).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn unienc_set_log_level(level: UniencLogLevel) {
    logging::init();
    logging::set_max_level(level.into());
}
