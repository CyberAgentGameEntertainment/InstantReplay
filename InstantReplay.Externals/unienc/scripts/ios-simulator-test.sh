#!/usr/bin/env bash
#
# Runs the unienc end-to-end harness inside an iOS simulator.
#
# A Rust test binary is a plain executable, so simctl can run one directly: no
# app bundle, no provisioning profile, no code signing. The runner that hands the
# binary to simctl is configured in .cargo/config.toml, so all this script has to
# do is make sure a simulator is booted first.
#
# VideoToolbox does encode H.264 in the simulator, so this covers the encoders'
# own logic. It does not cover the static library link, where the iOS build
# differs from macOS most, nor a real device's hardware encoder. Both still need
# a device and a signing identity.
#
# Usage:
#   scripts/ios-simulator-test.sh [<simulator name or UDID>]
#
# With no argument, any already booted simulator is used, otherwise the newest
# available iPhone is booted.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$here"

case "$(uname -m)" in
    arm64) target=aarch64-apple-ios-sim ;;
    x86_64) target=x86_64-apple-ios ;;
    *)
        echo "unsupported host architecture: $(uname -m)" >&2
        exit 1
        ;;
esac

wanted="${1:-}"
if [[ -n "$wanted" ]]; then
    device="$wanted"
    # Consumed here so that anything after it reaches cargo.
    shift
else
    device="$(xcrun simctl list devices booted | grep -oE '\(([0-9A-F-]{36})\)' | head -1 | tr -d '()' || true)"
    if [[ -z "$device" ]]; then
        # Newest runtime last, so the last iPhone listed is the newest. The
        # character class is spelled out because BSD grep does not take \s, and
        # `|| true` keeps a no-match from ending the script through `set -e`
        # before the message below can explain what is missing.
        device="$(xcrun simctl list devices available \
            | grep -E '^[[:space:]]+iPhone' | tail -1 \
            | grep -oE '\(([0-9A-F-]{36})\)' | head -1 | tr -d '()' || true)"
        if [[ -z "$device" ]]; then
            echo "no iPhone simulator is available; install one through Xcode" >&2
            exit 1
        fi
    fi
fi

echo "==> booting $device"
# Already booted is not an error worth stopping for.
xcrun simctl boot "$device" 2>/dev/null || true
xcrun simctl bootstatus "$device" -b

echo "==> testing on $target"
# The first run in a freshly booted simulator can take a minute or so while the
# media frameworks load for the first time.
rustup target add "$target" >/dev/null
exec cargo test -p unienc_testkit --target "$target" "$@"
