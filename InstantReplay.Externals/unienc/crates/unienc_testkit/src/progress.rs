//! Progress reporting that survives a hang.
//!
//! Stdout is block-buffered whenever it is not a terminal, and a device harness
//! runs behind a pipe: its output reaches `adb shell` only when the buffer fills
//! or the process exits. A harness that hangs never exits, so without flushing
//! every line, the run that most needs a trace produces none at all — which is
//! exactly what the first Android CI failure looked like.

use std::io::Write;

/// Prints one progress line and flushes it.
pub fn report(args: std::fmt::Arguments<'_>) {
    let mut out = std::io::stdout().lock();
    let _ = writeln!(out, "{args}");
    let _ = out.flush();
}

/// Reports progress in a way a hung run still shows. See [`report`].
#[macro_export]
macro_rules! progress {
    ($($arg:tt)*) => {
        $crate::progress::report(std::format_args!($($arg)*))
    };
}
