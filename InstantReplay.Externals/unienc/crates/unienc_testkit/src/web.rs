//! Emscripten-specific glue, so that the web runs the same harness as everyone
//! else.

use std::ffi::{CString, c_char};
use std::path::Path;

unsafe extern "C" {
    fn emscripten_run_script(script: *const c_char);
}

/// Redirects the muxer's browser download into the in-memory filesystem.
///
/// On the web the muxer hands its finished bytes to a download instead of
/// writing a file, so the shared driver would have nothing to read back. Rather
/// than give the backend a test-only mode, this intercepts the one JavaScript
/// function it calls and writes the same bytes to `path`. Nothing in
/// `unienc_webcodecs` knows the difference.
///
/// `window.unienc_webcodecs` does not exist yet at this point — the backend
/// creates it lazily when the first encoder is built — so the interception is a
/// property setter that patches the object as it is assigned and then replaces
/// itself with the plain value.
pub fn capture_muxed_output(path: &Path) -> Result<(), String> {
    let target = path.to_string_lossy();
    if target.contains('"') || target.contains('\\') {
        // The path is interpolated into a script; refuse anything needing care.
        return Err(format!(
            "output path is not usable from JavaScript: {target}"
        ));
    }

    let script = format!(
        r#"
        (function () {{
            const target = "{target}";
            const capture = function (partsPtr, numParts) {{
                const header = Module.HEAPU32.subarray(
                    partsPtr >> 2, (partsPtr >> 2) + numParts * 2);
                const parts = [];
                let total = 0;
                for (let i = 0; i < numParts; i++) {{
                    const ptr = header[i * 2];
                    const len = header[i * 2 + 1];
                    // Copied rather than viewed: the heap can be reallocated
                    // while this runs, which would leave a view dangling.
                    parts.push(Module.HEAPU8.slice(ptr, ptr + len));
                    total += len;
                }}
                const joined = new Uint8Array(total);
                let at = 0;
                for (const part of parts) {{
                    joined.set(part, at);
                    at += part.length;
                }}
                (Module.FS || FS).writeFile(target, joined);
            }};
            Object.defineProperty(window, "unienc_webcodecs", {{
                configurable: true,
                set: function (value) {{
                    value.makeDownload = capture;
                    Object.defineProperty(window, "unienc_webcodecs", {{
                        value: value,
                        writable: true,
                        configurable: true,
                    }});
                }},
            }});
        }})();
        "#
    );

    let script = CString::new(script).map_err(|error| error.to_string())?;
    // SAFETY: the script is a valid NUL-terminated C string.
    unsafe { emscripten_run_script(script.as_ptr()) };
    Ok(())
}
