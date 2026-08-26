#!/usr/bin/env bash
#
# Runs the unienc end-to-end harness in a browser, on WebAssembly.
#
# There is no headless JavaScript runtime that can stand in for a browser here:
# the WebCodecs backend drives the browser's own encoders and reaches for
# `window`, so the harness is a page. emrun serves it, forwards its stdout and
# turns its exit status into this script's.
#
# The build follows build-unienc.yml: nightly with build-std, because the
# Emscripten target has no prebuilt std, and mvp so the output runs where Unity's
# does.
#
# Requirements: EMSDK (an activated emsdk, i.e. `source emsdk_env.sh`), a nightly
# toolchain with rust-src, and Chrome or Chromium.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$here"

if ! command -v emcc >/dev/null; then
    echo "emcc is not on PATH; source your emsdk's emsdk_env.sh first" >&2
    exit 1
fi

profile=dev
profile_dir=debug
if [[ "${1:-}" == "--release" ]]; then
    # The wasm profile is where the workspace keeps its size settings.
    profile=release-wasm
    profile_dir=release-wasm
    shift
fi

# What the backend's own JavaScript reaches for has to be exported, or it fails
# inside a browser task where nothing reports it:
#   _malloc, _free       copying an encoded chunk out of the browser's encoder
#   HEAPU8, HEAPU32      the same copies, and reading the muxer's fragments back
#   UTF8ToString         reading string arguments
#   printErr             read by emrun's own injected code; a read of an
#                        unexported runtime method aborts the page
# The harness itself needs two more:
#   FORCE_FILESYSTEM, FS reading the muxed file back out of the in-memory
#                        filesystem, which a build making no filesystem calls
#                        would otherwise omit
#   EXIT_RUNTIME         reporting pass or fail as an exit status
export EMCC_CFLAGS="${EMCC_CFLAGS:-} -sEXIT_RUNTIME=1 -sALLOW_MEMORY_GROWTH=1 -sFORCE_FILESYSTEM=1 -sEXPORTED_FUNCTIONS=_main,_malloc,_free -sEXPORTED_RUNTIME_METHODS=FS,UTF8ToString,HEAPU8,HEAPU32,printErr --emrun"

# cargo does not treat EMCC_CFLAGS as a build input, so a changed link line would
# otherwise be ignored and the stale binary reused — which is hard to spot,
# because the symptom is the previous flags still being in effect.
flags_stamp="target/.emcc-cflags-$profile_dir"
if [[ ! -f "$flags_stamp" || "$(cat "$flags_stamp")" != "$EMCC_CFLAGS" ]]; then
    mkdir -p "$(dirname "$flags_stamp")"
    printf '%s' "$EMCC_CFLAGS" >"$flags_stamp"
    touch crates/unienc_harness_web/src/main.rs
fi

echo "==> building for wasm32-unknown-emscripten ($profile)"
rustup target add wasm32-unknown-emscripten >/dev/null
rustup component add rust-src --toolchain nightly >/dev/null
RUSTFLAGS="${RUSTFLAGS:-} -Ctarget-cpu=mvp" \
    cargo +nightly build -Z build-std=panic_abort,std \
    --target wasm32-unknown-emscripten --profile "$profile" \
    -p unienc_harness_web

module="target/wasm32-unknown-emscripten/$profile_dir/unienc_harness_web.js"
[[ -f "$module" ]] || {
    echo "missing $module" >&2
    exit 1
}

echo "==> running in a browser"
exec scripts/run-in-browser.sh "$module" "$@"
