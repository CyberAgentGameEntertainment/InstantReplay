#!/usr/bin/env bash
#
# Cargo runner for wasm32-unknown-emscripten: runs a test binary in a browser.
#
# The WebCodecs backend drives the browser's own encoders through
# `emscripten_run_script` and touches `window`, so there is no headless
# JavaScript runtime that can stand in for a real browser. emrun serves the page,
# forwards its stdout and reports its exit status, which is what makes a browser
# usable as a cargo test runner at all.
#
# emcc emits a .js module rather than a page, so a minimal HTML shell to load it
# is generated here. `Module.arguments` is how the test binary receives the
# arguments cargo appends, such as a test name filter or --nocapture.
#
# Invoked through .cargo/config.toml; see scripts/web-browser-test.sh for the
# wrapper that sets up the build.

set -euo pipefail

module="$1"
shift

if [[ ! -f "$module" ]]; then
    echo "no such module: $module" >&2
    exit 1
fi

browser="${UNIENC_TEST_BROWSER:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
if [[ ! -e "$browser" ]]; then
    # On Linux runners Chrome is on PATH under one of these names.
    for candidate in google-chrome chromium chromium-browser; do
        if command -v "$candidate" >/dev/null; then
            browser="$(command -v "$candidate")"
            break
        fi
    done
fi
if [[ ! -e "$browser" ]]; then
    echo "no browser found; set UNIENC_TEST_BROWSER" >&2
    exit 1
fi

# Cargo's arguments become the process arguments, as a JSON array for the shell.
arguments=""
for argument in "$@"; do
    escaped="${argument//\\/\\\\}"
    escaped="${escaped//\"/\\\"}"
    arguments+="\"$escaped\","
done

page="${module%.js}.html"
cat >"$page" <<HTML
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>unienc harness</title></head>
<body>
<script>
  var Module = { arguments: [${arguments%,}] };
</script>
<script async src="$(basename "$module")"></script>
</body>
</html>
HTML

# --kill-start clears any browser left behind by an interrupted run: it would
# still be showing the previous page and posting its output to this server, which
# is indistinguishable from the current run misbehaving.
# --kill-exit stops the browser once the page calls exit, otherwise emrun waits
# for a window that headless Chrome never shows. The silence timeout is the
# backstop for a page that fails before it can report anything.
# A stale server from an interrupted run would otherwise hold the default port
# and this would fail with nothing but a Python traceback.
exec emrun \
    --port "${UNIENC_TEST_PORT:-6931}" \
    --browser "$browser" \
    --browser-args="--headless=new --no-sandbox --disable-gpu --autoplay-policy=no-user-gesture-required" \
    --kill-start \
    --kill-exit \
    --silence-timeout "${UNIENC_TEST_SILENCE_TIMEOUT:-120}" \
    --timeout "${UNIENC_TEST_TIMEOUT:-300}" \
    "$page"
