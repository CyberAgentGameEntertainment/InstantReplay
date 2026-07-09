#!/usr/bin/env bash
# Localize all non-exported symbols in an Apple static library so that its
# bundled mimalloc does not coalesce with Unity 6.5's built-in mimalloc when the
# library is statically linked into UnityFramework (iOS `__Internal`).
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
MIN_OS="${5:-13.0}"      # -platform_version minimum OS version
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXPORTS="$SCRIPT_DIR/../../InstantReplay.Externals/unienc/crates/unienc_c/apple-exports.txt"

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
# too. Passing loose objects makes `-exported_symbols_list` behave as intended.
( cd "$WORK" && ar x "$LIB_ABS" )
member_count="$(ar t "$LIB_ABS" | grep -Ec '\.o$' || true)"
extracted_count="$(find "$WORK" -maxdepth 1 -name '*.o' | wc -l | tr -d ' ')"
if [ "$member_count" != "$extracted_count" ]; then
  echo "ERROR: extracted $extracted_count objects but archive lists $member_count (duplicate member names?)" >&2
  exit 1
fi

# Partial link into a single object.
#   -exported_symbols_list : demote every global NOT matching `_unienc*` to a
#                            private-extern symbol.
#   -r (without -keep_private_externs) : convert those private-externs (and the
#                            ones already produced by `-fvisibility=hidden`) into
#                            true local symbols.
#   -platform_version : required by ld-prime even for a `-r` link.
( cd "$WORK" && xcrun --sdk "$SDK" ld -r -arch "$ARCH" \
    -platform_version "$PLATFORM" "$MIN_OS" "$SDK_VER" \
    -exported_symbols_list "$EXPORTS" \
    *.o -o merged.o )

rm -f "$LIB_ABS"
xcrun libtool -static -o "$LIB_ABS" "$WORK/merged.o"

# --- Verify (CI gate; also guards against future linker behaviour changes) ---
echo "=== exported (global) symbols after localization ==="
nm -gU "$LIB_ABS" | sort -u

# (a) No mimalloc symbol may remain external or private-external. `nm -gU` lists
#     both (private-externs still coalesce), so it must contain zero of them.
if nm -gU "$LIB_ABS" | grep -iE '_mi_|_heap_main|_theap_main|mi_unity_'; then
  echo "FAIL: mimalloc symbols are still (private-)external -> would coalesce with Unity" >&2
  exit 1
fi

# (b) FFI entry points must remain global.
if ! nm -gU "$LIB_ABS" | grep -q '_unienc_UnityPluginLoad'; then
  echo "FAIL: _unienc_UnityPluginLoad is no longer exported" >&2
  exit 1
fi

echo "Localization OK: $(nm -gU "$LIB_ABS" | grep -c '_unienc') unienc symbols exported, 0 mimalloc symbols external"
