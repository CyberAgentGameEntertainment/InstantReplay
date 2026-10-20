# unienc

A Rust-based unified encoding system that provides cross-platform video and audio encoding capabilities. It's part of the larger InstantReplay project and serves as an external dependency for Unity-based instant replay functionality.

## Supported Platforms

| Platform | Video | Audio | Muxing | GPU Texture |
|----------|-------|-------|--------|-------------|
| **Apple** (macOS / iOS) | VideoToolbox | AudioToolbox | AVFoundation | Metal |
| **Android** | MediaCodec | MediaCodec | MediaMuxer | Vulkan |
| **Windows** | Media Foundation | Media Foundation | Media Foundation | - |
| **Linux / Unix** | FFmpeg | FFmpeg | FFmpeg | - |
| **WebAssembly** (Emscripten) | WebCodecs API | WebCodecs API | muxide (MP4) | - |

## Prerequisites

- A stable Rust toolchain installed through rustup, with the target of each platform to build (`rustup target add <target>`).
- The git submodules under `external/` (`git submodule update --init --recursive` at the root of the repository).
- Additional tools for each platform:

| Platform | Requirements |
|----------|--------------|
| Android | Android NDK (`ANDROID_NDK_HOME`), `cargo-ndk` (`cargo install cargo-ndk`). For the tests: Android SDK (`ANDROID_HOME`), a JDK and the build-tools for `d8`. |
| iOS | Xcode. For the tests: an iOS simulator runtime. |
| Linux | `gcc-multilib`. For the tests: the `ffmpeg` executable with libx264 and an AAC encoder. |
| WebAssembly | A nightly toolchain with `rust-src` (`rustup toolchain install nightly --component rust-src`), an activated emsdk (`source emsdk_env.sh`) and TypeScript (`tsc`). For the tests: Google Chrome. Chromium does not work because it does not ship the H.264 codec. |

## Build

### Variants

The native library is built in two variants from `crates/unienc_c`.

| Variant | Cargo features | Destination |
|---------|----------------|-------------|
| Unity | `-F unity,mimalloc` | `Packages/jp.co.cyberagent.instant-replay/UniEnc/Plugins/<target>/` |
| NuGet | default features | `InstantReplay.Externals/src/UniEnc/Plugins/<target>/` |

The NuGet variant omits the Unity-dependent features. The WebAssembly build is produced only for the Unity variant.

### Building Locally

The commands below are run in `crates/unienc_c` and correspond to the jobs of `.github/workflows/build-unienc.yml` at the root of the repository, which is the authoritative build configuration. Replace `--release` with nothing for a debug build, and omit `-F unity,mimalloc` for the NuGet variant.

```sh
# macOS (each architecture)
cargo build --target aarch64-apple-darwin --release -F unity,mimalloc
cargo build --target x86_64-apple-darwin --release -F unity,mimalloc

# iOS
CFLAGS_aarch64-apple-ios=-fno-common IPHONEOS_DEPLOYMENT_TARGET=10.0 \
  cargo build --target aarch64-apple-ios --release -F unity,mimalloc

# Android (each ABI)
cargo ndk -t aarch64-linux-android --platform 26 build --release -F unity,mimalloc

# Windows
cargo build --target x86_64-pc-windows-msvc --release -F unity,mimalloc

# Linux
cargo build --target x86_64-unknown-linux-gnu --release -F unity,mimalloc
```

The WebAssembly build is run in the root of the workspace:

```sh
RUSTFLAGS=-Ctarget-cpu=mvp cargo +nightly rustc -Z json-target-spec -Z build-std=panic_abort,std \
  --target wasm32-unknown-emscripten --profile release-wasm --no-default-features --crate-type staticlib -p unienc_c
```

The outputs are written to `target/<target>/<profile>/`. The workflow applies the following post-processing before placing them in the destination directories:

- macOS (Unity variant): The two architectures are combined into a universal binary with `lipo -create -output libunienc_c.bundle ...` and placed in `apple-darwin/`. The NuGet variant keeps one `.dylib` per architecture.
- iOS (Unity variant): `.github/scripts/localize-apple-staticlib.sh libunienc_c.a arm64 iphoneos` localizes the non-exported symbols, so that the bundled mimalloc does not conflict with the mimalloc built into Unity when the library is linked statically into UnityFramework. `-fno-common` above is required for the same reason.
- Windows: `unienc_c.dll` is renamed to `libunienc_c.dll`.
- Destination directory names drop the vendor and ABI suffix of some targets: `x86_64-pc-windows`, `x86_64-unknown-linux`.

### Updating the Committed Binaries

The native binaries committed to the repository must be produced by the `build-unienc.yml` workflow. Run it with `workflow_dispatch` on the branch to update. The workflow builds every platform and variant, places the binaries, regenerates `THIRD-PARTY-NOTICES.md`, writes the source fingerprint, and commits the result to the branch.

The source fingerprint (`Packages/jp.co.cyberagent.instant-replay/UniEnc/.source-fingerprint`) is computed by `.github/scripts/compute-unienc-fingerprint.sh` from the git object hashes of this workspace (including the submodule pointers), `build-unienc.yml` and `localize-apple-staticlib.sh`. On a pull request that changes the package version, `check-native-binaries.yml` recomputes the fingerprint and fails if it differs from the committed one, which means that the binaries have not been rebuilt since the sources changed.

Binaries built locally are useful for development and debugging, but are not to be committed, because the fingerprint would not be updated together with them.

## Testing

`crates/unienc_testkit` contains a shared end-to-end harness that drives the encoding system of the host platform (video encoder, audio encoder and muxer). The GPU texture path is not covered because it requires a Unity graphics device. Muxed outputs are written under `target/` for inspection.

| Platform | Command (run in the root of the workspace) |
|----------|--------------------------------------------|
| macOS | `cargo test -p unienc_testkit -p unienc_common -p unienc_apple_vt` |
| Windows | `cargo test -p unienc_testkit -p unienc_common -p unienc_windows_mf` |
| Linux | `cargo test -p unienc_testkit -p unienc_common -p unienc_ffmpeg` |
| iOS simulator | `scripts/ios-simulator-test.sh [<simulator name or UDID>]` |
| Android device or emulator | `scripts/android-device-test.sh [--release] [-s <adb serial>]` |
| WebAssembly | `scripts/web-browser-test.sh [--release]` |

- iOS: The test binary is run directly in a booted simulator through `simctl`, so no code signing is required. The script boots a simulator if none is running. The static library link and the hardware encoder of a real device are not covered.
- Android: The test binary is run through `app_process` on the device, so no APK is built. The same script works on an emulator and on a real device; the emulator uses a software encoder, so vendor-specific behavior of hardware encoders is covered only on a real device.
- WebAssembly: The harness is run as a page in Chrome through emrun. Set `UNIENC_TEST_BROWSER` to use a browser at a non-default location.

`cargo test` does not build the FFI layer, so also run `cargo check -p unienc_c` after changing the signatures of a platform backend. CI additionally checks the formatting with `cargo fmt --all -- --check`. See `.github/workflows/ci-unienc.yml` at the root of the repository for the CI configuration.

## Third-Party Notices

`THIRD-PARTY-NOTICES.md` at the root of the repository lists the licenses of the Rust dependencies linked into the Unity variant. It is generated with cargo-about, using `about.toml` (the list of accepted licenses) and `about.hbs` (the template). The `build-unienc.yml` workflow regenerates it on every build. To regenerate it locally, run the following in the root of the workspace:

```sh
cargo install cargo-about --locked --features cli
cargo about generate --features unity,mimalloc about.hbs > ../../THIRD-PARTY-NOTICES.md
```

If a new dependency uses a license that is not in `about.toml`, the generation fails. Review the license before adding it to the accepted list.

## Architecture

The codebase follows a modular architecture with platform-specific implementations behind a unified trait interface.

```
Unity C#
  ↓ (FFI via csbindgen)
unienc_c (C FFI layer)
  ↓
unienc (platform selector via cfg)
  ↓
┌──────────────────────────────────────────────┐
│ apple_vt │ android_mc │ windows_mf │ ffmpeg  │
│                webcodecs                     │
└──────────────────────────────────────────────┘
```

### Crates

- `crates/unienc_common/` — Common traits and interfaces (`EncodingSystem`, `Encoder`, `Muxer`, etc.)
- `crates/unienc/` — Conditionally selects the platform-specific implementation at compile time
- `crates/unienc_c/` — C FFI entrypoint for Unity. `csbindgen` auto-generates C# bindings (`NativeMethods.g.cs`)
- `crates/unienc_apple_vt/` — Apple VideoToolbox / AudioToolbox / AVFoundation
- `crates/unienc_android_mc/` — Android MediaCodec / MediaMuxer (JNI + Vulkan)
- `crates/unienc_windows_mf/` — Windows Media Foundation
- `crates/unienc_ffmpeg/` — FFmpeg for Linux and other Unix-like systems
- `crates/unienc_webcodecs/` — WebCodecs API via Emscripten for WebAssembly builds

### External Dependencies

The following crates are included as git submodules and replace the crates.io versions through `[patch.crates-io]` in `Cargo.toml`.

- `external/muxide/` — MP4 multiplexer (used by `unienc_webcodecs`)
- `external/mimalloc_rust/` — Fork of the mimalloc allocator bindings (used by the `mimalloc` feature)

### Key Traits

- `EncodingSystem` — Factory for creating encoders and muxers
- `Encoder` — Handles encoding of raw samples to compressed format
- `Muxer` — Combines encoded audio/video streams into a container format

## Unity Integration

`unienc_c` exposes C FFI functions that are consumed by the Unity package `jp.co.cyberagent.instant-replay`. The C# bindings are auto-generated by `csbindgen` into `NativeMethods.g.cs` under the `UniEnc.Native` namespace.

- On native platforms, the library is loaded as `libunienc_c`
- On iOS and WebGL (non-Editor), it links statically via `__Internal`
