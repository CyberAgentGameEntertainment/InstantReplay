//! Logging back-end for the whole of UniEnc.
//!
//! Every crate emits diagnostics through the `log` facade. This module installs the single
//! `log::Log` implementation that decides where those records actually go:
//!
//! - When the `unity` feature is on and Unity has handed us `IUnityLog`, into the Unity log. That
//!   reaches more than the log file: the Editor console, and the console of an Editor attached to a
//!   development player. Android included — Unity's own log destination there *is* logcat, under
//!   the `Unity` tag, so nothing is given up by preferring this over writing to logcat ourselves.
//! - Otherwise on Android, straight to logcat through `__android_log_write` under the `unienc` tag.
//!   `JNI_OnLoad` runs long before `UnityPluginLoad`, and Android discards a process's stdout, so
//!   without this the earliest records — and any emitted after `UnityPluginUnload` — would be lost
//!   outright rather than merely landing somewhere less convenient.
//! - Otherwise (NuGet package, CLI tests, or before Unity has loaded the plugin) to stdout/stderr.
//!
//! The sink is chosen per record rather than at install time, so records emitted before Unity calls
//! `UnityPluginLoad` fall back instead of being dropped, and records emitted after
//! `UnityPluginUnload` stop using an interface that is no longer ours to use.
//!
//! One cost to be aware of on the Unity sink: Unity captures a managed stack trace per record
//! wherever `Application.GetStackTraceLogType` says to, which is `ScriptOnly` for `LogType.Log` in
//! a development build and `None` in a release one. High-volume `debug!`/`trace!` output from the
//! encoder therefore pays a stack walk per record in a development build.

use log::{LevelFilter, Log, Metadata, Record};
use std::sync::Once;

/// Identifier carried by every record so UniEnc output stays greppable once it is mixed into a
/// shared log (the Unity player log, or logcat when another tag is in use).
const LOG_TAG: &str = "unienc";

static INIT: Once = Once::new();
static LOGGER: UniencLogger = UniencLogger;

/// Installs the UniEnc logger. Idempotent, and safe to call from every entry point that may be the
/// first one to run: `UnityPluginLoad`, `JNI_OnLoad`, and `unienc_new_runtime`.
pub fn init() {
    INIT.call_once(|| {
        log::set_max_level(if cfg!(debug_assertions) {
            LevelFilter::Debug
        } else {
            LevelFilter::Warn
        });
        // A logger installed by the host application wins; UniEnc must not fight it.
        let _ = log::set_logger(&LOGGER);
    });
}

/// Overrides the level below which records are discarded.
pub fn set_max_level(level: LevelFilter) {
    log::set_max_level(level);
}

struct UniencLogger;

impl Log for UniencLogger {
    fn enabled(&self, metadata: &Metadata) -> bool {
        metadata.level() <= log::max_level()
    }

    fn log(&self, record: &Record) {
        if !self.enabled(record.metadata()) {
            return;
        }

        #[cfg(feature = "unity")]
        if unity::write(record) {
            return;
        }

        #[cfg(target_os = "android")]
        android::write(record);

        #[cfg(not(target_os = "android"))]
        write_to_stdio(record);
    }

    fn flush(&self) {}
}

/// `{target}: {message}` — the crate/module path is what tells apart an encoder failure from a
/// muxer one, and it is not otherwise visible in any of the sinks.
fn format_body(record: &Record) -> String {
    format!("{}: {}", record.target(), record.args())
}

#[cfg(not(target_os = "android"))]
fn write_to_stdio(record: &Record) {
    // stdout has no severity channel, so the level has to be part of the text.
    let line = format!("[{}][{}] {}", LOG_TAG, record.level(), format_body(record));
    if record.level() <= log::Level::Warn {
        eprintln!("{line}");
    } else {
        println!("{line}");
    }
}

/// Builds a `CString` that never fails: interior NUL bytes are replaced rather than rejected, so a
/// malformed message degrades to readable text instead of vanishing.
#[cfg(any(target_os = "android", feature = "unity"))]
fn to_c_string(value: &str) -> std::ffi::CString {
    match std::ffi::CString::new(value) {
        Ok(value) => value,
        Err(err) => {
            let mut bytes = err.into_vec();
            for byte in bytes.iter_mut() {
                if *byte == 0 {
                    *byte = b'?';
                }
            }
            // Every NUL is gone, so this cannot fail.
            std::ffi::CString::new(bytes).unwrap()
        }
    }
}

#[cfg(target_os = "android")]
mod android {
    use super::{LOG_TAG, format_body, to_c_string};
    use log::{Level, Record};
    use ndk_sys::__android_log_write;
    use std::ffi::c_int;

    const ANDROID_LOG_VERBOSE: c_int = 2;
    const ANDROID_LOG_DEBUG: c_int = 3;
    const ANDROID_LOG_INFO: c_int = 4;
    const ANDROID_LOG_WARN: c_int = 5;
    const ANDROID_LOG_ERROR: c_int = 6;

    pub fn write(record: &Record) {
        let priority = match record.level() {
            Level::Error => ANDROID_LOG_ERROR,
            Level::Warn => ANDROID_LOG_WARN,
            Level::Info => ANDROID_LOG_INFO,
            Level::Debug => ANDROID_LOG_DEBUG,
            Level::Trace => ANDROID_LOG_VERBOSE,
        };
        let tag = to_c_string(LOG_TAG);
        let message = to_c_string(&format_body(record));
        unsafe {
            __android_log_write(priority, tag.as_ptr(), message.as_ptr());
        }
    }
}

#[cfg(feature = "unity")]
mod unity {
    use super::{LOG_TAG, format_body, to_c_string};
    use log::{Level, Record};
    use std::sync::OnceLock;
    use std::sync::atomic::{AtomicBool, Ordering};
    use unity_native_plugin::interface::UnityInterfaces;
    use unity_native_plugin::log::{IUnityLog, LogType, UnityLog};

    /// `UnityInterfaces::get()` panics until Unity has handed the interfaces over, and there is no
    /// fallible accessor, so availability has to be tracked here.
    static INTERFACES_READY: AtomicBool = AtomicBool::new(false);
    /// `None` once resolved means this Unity version does not expose `IUnityLog`.
    static UNITY_LOG: OnceLock<Option<UnityLog>> = OnceLock::new();

    /// Called from `UnityPluginLoad`, after the interfaces have been stored.
    pub fn set_available(available: bool) {
        INTERFACES_READY.store(available, Ordering::Release);
    }

    /// Returns `false` when Unity logging is unavailable and the caller should fall back.
    pub fn write(record: &Record) -> bool {
        if !INTERFACES_READY.load(Ordering::Acquire) {
            return false;
        }
        let Some(unity_log) = UNITY_LOG
            .get_or_init(|| UnityInterfaces::get().interface::<UnityLog>())
            .as_ref()
        else {
            return false;
        };

        let log_type = match record.level() {
            Level::Error => LogType::Error,
            Level::Warn => LogType::Warning,
            // Unity has no debug/trace channel; those are already filtered by `max_level`.
            Level::Info | Level::Debug | Level::Trace => LogType::Log,
        };
        let message = to_c_string(&format!("[{}] {}", LOG_TAG, format_body(record)));
        let file = to_c_string(record.file().unwrap_or("<unknown>"));
        unity_log.log(log_type, &message, &file, record.line().unwrap_or(0) as i32);
        true
    }
}

/// Notifies the logger that Unity has provided (or withdrawn) its plugin interfaces.
#[cfg(feature = "unity")]
pub fn set_unity_interfaces_available(available: bool) {
    unity::set_available(available);
}
