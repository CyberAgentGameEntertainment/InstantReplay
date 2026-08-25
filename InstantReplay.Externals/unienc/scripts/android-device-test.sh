#!/usr/bin/env bash
#
# Runs the unienc end-to-end harness on a connected Android device or emulator.
#
# There is no APK and no Gradle project. MediaCodec and MediaMuxer need a JavaVM
# but neither an Activity nor a Context, so a JVM is the only thing a shell is
# missing, and `app_process` supplies one. The same command therefore works
# against an emulator and a real device, which matters because the defects worth
# catching here are the ones specific to a vendor's hardware encoder.
#
# The GPU blit path is not covered: it needs a Unity graphics device.
#
# Usage:
#   scripts/android-device-test.sh [--release] [-s <adb serial>]
#
# Requirements: ANDROID_HOME (or ANDROID_SDK_ROOT), ANDROID_NDK_HOME, cargo-ndk,
# a JDK for javac, and adb build-tools for d8.

set -euo pipefail

profile=dev
profile_dir=debug
serial=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --release)
            profile=release
            profile_dir=release
            shift
            ;;
        -s)
            serial="$2"
            shift 2
            ;;
        *)
            echo "unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$here"

sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
if [[ -z "$sdk" ]]; then
    echo "set ANDROID_HOME to the Android SDK" >&2
    exit 1
fi
if [[ -z "${ANDROID_NDK_HOME:-}" ]]; then
    # cargo-ndk needs this and gives a less obvious error without it.
    echo "set ANDROID_NDK_HOME to an NDK under $sdk/ndk" >&2
    exit 1
fi

# Kept as one array including the binary so that an empty serial does not
# expand to an unset element, which bash 3.2 rejects under `set -u`.
adb=("$sdk/platform-tools/adb")
if [[ -n "$serial" ]]; then
    adb+=(-s "$serial")
fi
# Any recent build-tools release will do; take the highest installed.
build_tools="$(ls -1 "$sdk/build-tools" | sort -V | tail -1)"
d8="$sdk/build-tools/$build_tools/d8"

# The library has to match the device, not the host.
abi="$("${adb[@]}" shell getprop ro.product.cpu.abi | tr -d '\r')"
case "$abi" in
    arm64-v8a) triple=aarch64-linux-android ;;
    armeabi-v7a) triple=armv7-linux-androideabi ;;
    x86_64) triple=x86_64-linux-android ;;
    *)
        echo "unsupported device ABI: $abi" >&2
        exit 1
        ;;
esac

echo "==> device ABI $abi, building $profile"
# --platform 26 matches the minimum the Java API bindings are checked against.
cargo ndk -t "$abi" --platform 26 build --profile "$profile" -p unienc_harness_android

library="target/$triple/$profile_dir/libunienc_harness_android.so"
[[ -f "$library" ]] || {
    echo "missing $library" >&2
    exit 1
}

echo "==> building the JVM shim"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT
java_src=crates/unienc_harness_android/java/jp/co/cyberagent/unienc/harness/Harness.java
# The shim only touches java.lang, so it needs no android.jar to compile.
javac --release 11 -d "$staging/classes" "$java_src"
"$d8" --min-api 26 --output "$staging" \
    "$staging/classes/jp/co/cyberagent/unienc/harness/Harness.class"

remote=/data/local/tmp/unienc-harness
echo "==> pushing to $remote"
"${adb[@]}" shell "rm -rf $remote && mkdir -p $remote"
"${adb[@]}" push "$library" "$remote/libunienc_harness_android.so" >/dev/null
"${adb[@]}" push "$staging/classes.dex" "$remote/harness.dex" >/dev/null

echo "==> running"
# Start from an empty log so the dump below only holds this run, and so a native
# crash or an ANR is attributable.
"${adb[@]}" logcat -c 2>/dev/null || true

# The harness runs under the device's own `timeout` rather than a host-side one,
# because what hangs is the process on the device. Without this a hang holds the
# job until its overall timeout and leaves nothing to look at. TERM first, then
# KILL, so a process ignoring TERM still goes.
#
# app_process wants a "parent directory" argument it does not use for anything
# here, and finds the shim through CLASSPATH.
set +e
"${adb[@]}" shell "cd $remote && timeout -s KILL ${UNIENC_TEST_TIMEOUT:-300} env CLASSPATH=$remote/harness.dex app_process $remote jp.co.cyberagent.unienc.harness.Harness $remote/libunienc_harness_android.so $remote/e2e.mp4; echo EXIT:\$?" \
    | tr -d '\r' | tee "$staging/output"
set -e

status="$(grep '^EXIT:' "$staging/output" | tail -1 | cut -d: -f2)"

# `timeout` reports 137 for a KILL. Say so plainly: the distinction between a
# hang and a failed check matters more than the exit code.
if [[ "${status:-1}" == "137" ]]; then
    echo "==> the harness did not finish within ${UNIENC_TEST_TIMEOUT:-300}s and was killed" >&2
fi

# Keep the muxed file for inspection whether or not the checks passed.
if "${adb[@]}" shell "test -f $remote/e2e.mp4" 2>/dev/null; then
    mkdir -p target/android-harness
    "${adb[@]}" pull "$remote/e2e.mp4" target/android-harness/e2e.mp4 >/dev/null
    echo "==> pulled target/android-harness/e2e.mp4"
fi

# On failure the device log is the only place a native crash, a missing library
# or an ART complaint shows up; the harness's own output stops at whatever it
# managed to print.
if [[ "${status:-1}" != "0" ]]; then
    mkdir -p target/android-harness
    "${adb[@]}" logcat -d > target/android-harness/logcat.txt 2>/dev/null || true
    echo "==> device log saved to target/android-harness/logcat.txt" >&2
    # Narrow to the codec and process machinery. A `google_apis` image runs Play
    # services, whose logging drowns out everything else, and the harness's own
    # output goes to stdout rather than here.
    echo "--- last 40 relevant lines ---" >&2
    grep -aiE "unienc|harness|app_process|AndroidRuntime|DEBUG *:|CCodec|Codec2|MediaCodec|OMX|c2\.android|ACodec|BufferQueue" \
        target/android-harness/logcat.txt | tail -40 >&2 || true
fi

exit "${status:-1}"
