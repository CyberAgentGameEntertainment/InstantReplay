# Testing unienc without Unity

Building a Unity player to check an encoder change is slow, so the end-to-end
harness runs standalone on every platform it can. `unienc_testkit` holds one
pipeline definition and one set of assertions; each platform only supplies a way
to start it. That is the point: a backend cannot pass on a build host for reasons
that would not hold on a phone.

What every platform runs is the same: encode ten seconds of colour bars and a
tone, mux them, then read the resulting MP4 back and check it. See
`crates/unienc_testkit/src/verify.rs` for the assertions.

## What this does not cover

- **The GPU blit path.** `is_blit_supported()` requires a Unity graphics device,
  so a standalone harness always exercises the CPU readback path. Testing blit
  needs Unity.
- **The static library link.** On iOS the shipped artifact is a `.a` linked into
  UnityFramework, with mimalloc symbols localized (see `build-unienc.yml`). The
  simulator harness builds its own executable and does not go through that.
- **Vendor hardware encoders.** An emulator and a simulator use software or host
  encoders. The frame drops, size alignment faults and colour shifts that have
  needed fixing before are specific to a device's own encoder, so a real device
  is still the only place they show up.

## Desktop

```bash
cargo test -p unienc_testkit -p unienc_common -p <backend crate>
```

`<backend crate>` is `unienc_apple_vt` on macOS, `unienc_windows_mf` on Windows
and `unienc_ffmpeg` on Linux; a platform backend only builds for its own
platform, so its unit tests can only run there. On Linux the FFmpeg backend
shells out to `ffmpeg`, which has to be installed and to have an H.264 and an AAC
encoder. This is what CI runs (`.github/workflows/ci-unienc.yml`).

## iOS simulator

```bash
scripts/ios-simulator-test.sh
```

A Rust test binary is a plain executable, so `simctl` runs one directly — no app
bundle, no provisioning profile, no code signing. The runner that hands the
binary to `simctl` lives in `.cargo/config.toml`, so once a simulator is booted,
plain `cargo test -p unienc_testkit --target aarch64-apple-ios-sim` works too.

VideoToolbox does encode H.264 in the simulator, so this covers the encoder logic
rather than just compilation. The first run after booting a simulator can take a
minute while the media frameworks load; later runs take a few seconds.

Most of the Apple backend is shared between iOS and macOS, so the desktop run
already covers it. What the simulator adds is the iOS deployment target and the
iOS variants of the frameworks.

## Android device or emulator

```bash
export ANDROID_HOME=~/Library/Android/sdk
export ANDROID_NDK_HOME="$ANDROID_HOME/ndk/<version>"
scripts/android-device-test.sh          # add -s <serial> to pick a device
```

There is no APK and no Gradle project. MediaCodec and MediaMuxer need a `JavaVM`
but neither an `Activity` nor a `Context`, so a JVM is the only thing an `adb`
shell is missing, and `app_process` supplies one. The script builds
`unienc_harness_android` as a shared library, compiles a small Java shim to a
dex, pushes both, and runs them:

```
CLASSPATH=…/harness.dex app_process … jp.co.cyberagent.unienc.harness.Harness …
```

`System.load` in the shim is what calls `JNI_OnLoad`, which is where the backend
picks up the `JavaVM`. The muxed file is pulled back to
`target/android-harness/e2e.mp4` whether or not the checks passed.

The same command works against an emulator and a real device, which is the reason
for this shape rather than an instrumented test: reaching for a device when a
result looks suspicious costs nothing extra.

### A note on track duration

MediaMuxer derives sample durations from the gaps between presentation
timestamps, and has nothing to derive the last one from, so it gives the final
sample a duration of zero and the video track ends one frame interval short.
AVFoundation and FFmpeg give it a full interval. The harness allows for this; the
frame count is the strict check.

## Web

```bash
source /path/to/emsdk/emsdk_env.sh
scripts/web-browser-test.sh          # add --release for the release-wasm profile
```

This is the one target where the harness is a program rather than a `cargo test`.
The encoders are the browser's own and report through callbacks delivered as
browser tasks, so the only thread has to keep returning to the event loop, while
libtest expects a test function to run to completion. `unienc_harness_web`
therefore drives the pipeline the way Unity does in production: a callback per
animation frame, polling whatever has become ready.
`emscripten_set_main_loop` here plays the part `unienc_tick_runtime` plays there.

ASYNCIFY looks like an easier answer and is not one. It unwinds the wasm stack
while suspended, and the encoder callbacks re-enter wasm during exactly that
window, which is the reentrancy it does not support. Trying it deadlocks.

Two more things are specific to this target:

- **The runtime has no threads.** Emscripten is built here without pthreads, so
  `TestRuntime` uses a `LocalPool` rather than a thread pool, matching the
  `--no-default-features` build `unienc_c` ships for the web. Blocking work runs
  inline because there is nowhere to offload it to.
- **The muxer downloads its output instead of writing a file**, so there would be
  nothing for the verification to read. Rather than give the backend a test-only
  mode, `unienc_testkit::web::capture_muxed_output` intercepts the one JavaScript
  function the muxer calls and writes the same bytes into the in-memory
  filesystem. Nothing in `unienc_webcodecs` knows the difference.

emrun serves the page, forwards its stdout and turns the harness's exit status
into the script's. Chrome's software H.264 encoder (OpenH264) does the encoding on
a machine without hardware support.

The link flags in `scripts/web-browser-test.sh` are not incidental. The backend's
own JavaScript reaches for `_malloc`, `_free` and the heap views, and it does so
from inside an encoder callback — a browser task, where a `TypeError` is reported
nowhere the harness can see. Omitting those exports does not fail the build or
raise an error: encoded chunks simply never arrive, and the run hangs. The page
errors the runner's HTML forwards to stdout exist for the same reason.

`cargo` does not treat `EMCC_CFLAGS` as a build input, so a change to the link
flags would leave the previous binary in place and appear to have no effect. The
script records the flags it used and forces a relink when they differ.

### Known open issue

The browser run does not pass yet. It gets as far as encoding both streams — ten
video frames in and ten encoded frames out, ten audio chunks accepted — and then
the muxer refuses the first audio frame:

```
Failed to write encoded frame: audio frame arrived before any video frame:
write at least one video frame before writing audio
```

`muxide`, which the WebCodecs backend muxes with, will not take audio before the
first video frame. The pipeline pushes both streams concurrently, so which
arrives first depends on the encoders, and on the web the audio wins. The other
backends' muxers accept either order, so this is specific to this one. Whether to
hold audio back in `WebCodecsMuxer` until the first video frame lands, or to
relax the constraint in `muxide`, is a decision about the backend rather than
about the harness.

## Real devices

Both mobile harnesses run unchanged on real hardware, but neither is wired into
CI:

- **Android**: connect a device with USB debugging and pass `-s <serial>`.
- **iOS**: a real device needs the binary wrapped in a signed app bundle.
  `cargo-dinghy` automates that, at the cost of a signing identity. An Xcode test
  target linking `libunienc_c.a` is the other option, and the only way to cover
  the static library link described above.

Run these before a release, and whenever a change touches a platform backend's
interaction with the vendor encoder.
