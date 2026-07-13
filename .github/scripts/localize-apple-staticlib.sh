#!/usr/bin/env bash
# Localize the bundled mimalloc symbols in an Apple static library so they do
# not coalesce with Unity 6.5's built-in mimalloc when the library is statically
# linked into UnityFramework (iOS `__Internal`).
#
# Only mimalloc's own symbols are localized. Rust runtime symbols (including
# personality routines) are intentionally left global: localizing them would
# give every linked static lib its own personality routine, and compact-unwind
# can encode at most 3 per image ("ld: Too many personality routines ...").
#
# Requires the library to have been compiled with `-fno-common` (see the CI
# workflow) so that mimalloc's tentative definitions (e.g. `_mi_page_map`) are
# real defined symbols rather than common symbols, which cannot be localized.
#
# Usage: localize-apple-staticlib.sh <lib.a> <ld-arch> [sdk] [platform] [min-os]
#   e.g. localize-apple-staticlib.sh libunienc_c.a arm64 iphoneos ios 13.0
set -euo pipefail

LIB="$1"                 # path to the .a (e.g. libunienc_c.a)
ARCH="$2"                # ld arch name (e.g. arm64)
SDK="${3:-iphoneos}"     # xcrun SDK (e.g. iphoneos)
PLATFORM="${4:-ios}"     # -platform_version platform name (e.g. ios)
MIN_OS="${5:-10.0}"      # -platform_version minimum OS version
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNEXPORTS="$SCRIPT_DIR/../../InstantReplay.Externals/unienc/crates/unienc_c/apple-mimalloc-unexports.txt"

if [ ! -f "$LIB" ]; then
  echo "ERROR: static library not found: $LIB" >&2
  exit 1
fi

LIB_ABS="$(cd "$(dirname "$LIB")" && pwd)/$(basename "$LIB")"
SDK_VER="$(xcrun --sdk "$SDK" --show-sdk-version)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Extract every archive member as a loose object. We deliberately do NOT use
# `ld -r -force_load <archive>`: the modern linker (ld-prime) localizes *all*
# symbols of a force-loaded archive, which would strip the `_unienc*` exports
# and the Rust runtime symbols too. Loose objects make the symbol lists behave.
( cd "$WORK" && ar x "$LIB_ABS" )
member_count="$(ar t "$LIB_ABS" | grep -Ec '\.o$' || true)"
extracted_count="$(find "$WORK" -maxdepth 1 -name '*.o' | wc -l | tr -d ' ')"
if [ "$member_count" != "$extracted_count" ]; then
  echo "ERROR: extracted $extracted_count objects but archive lists $member_count (duplicate member names?)" >&2
  exit 1
fi

# Partial link into a single object.
#   -unexported_symbols_list : demote ONLY the listed mimalloc symbols to
#                              private-extern (leaves everything else global).
#   -r (without -keep_private_externs) : convert those private-externs (and the
#                              ones already produced by `-fvisibility=hidden`)
#                              into true local symbols.
#   -platform_version : required by ld-prime even for a `-r` link.
( cd "$WORK" && xcrun --sdk "$SDK" ld -r -arch "$ARCH" \
    -platform_version "$PLATFORM" "$MIN_OS" "$SDK_VER" \
    -unexported_symbols_list "$UNEXPORTS" \
    *.o -o merged.o )

rm -f "$LIB_ABS"
xcrun libtool -static -o "$LIB_ABS" "$WORK/merged.o"

# --- Verify (CI gate; also guards against future linker behaviour changes) ---
# (a) No mimalloc symbol may remain external or private-external. `nm -gU` lists
#     both (private-externs still coalesce), so it must contain zero of them.
#     The `_mi_` substring also matches `__mi_*` / `___mi_*`.
if xcrun nm -gU "$LIB_ABS" | grep -iE '_mi_|_heap_main|_theap'; then
  echo "FAIL: mimalloc symbols are still (private-)external -> would coalesce with Unity" >&2
  echo "      (if _mi_page_map is listed, the build is missing -fno-common)" >&2
  exit 1
fi

# (b) FFI entry points must remain global. Use `grep -c` (not `grep -q`): under
# `set -o pipefail`, `grep -q` exits on first match and SIGPIPEs `nm`, whose
# non-zero status would then be reported as a (spurious) failure.
if [ "$(xcrun nm -gU "$LIB_ABS" | grep -c '_unienc_UnityPluginLoad')" -eq 0 ]; then
  echo "FAIL: _unienc_UnityPluginLoad is no longer exported" >&2
  exit 1
fi

echo "Localization OK: $(xcrun nm -gU "$LIB_ABS" | grep -c '_unienc') unienc symbols exported, 0 mimalloc symbols external"
