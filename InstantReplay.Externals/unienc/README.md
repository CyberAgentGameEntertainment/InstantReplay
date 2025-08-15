# unienc

A Rust-based unified encoding system that provides cross-platform video and audio encoding capabilities. It's part of the larger InstantReplay project and serves as an external dependency for Unity-based instant replay functionality.

The project implements platform-specific encoders:
- **Apple platforms**: VideoToolbox for video, AudioToolbox for audio, AVFoundation for muxing
- **Android**: MediaCodec for video/audio encoding and MediaMuxer for muxing
- **Windows**: Media Foundation (implementation in progress)

## Architecture

The codebase follows a modular architecture with platform-specific implementations:

- `crates/unienc_common/`: Defines common traits and interfaces (`EncodingSystem`, `Encoder`, `Muxer`, etc.)
- `crates/unienc_apple_vt/`: Apple VideoToolbox/AudioToolbox implementation
- `crates/unienc_android_mc/`: Android MediaCodec implementation
- `crates/unienc_windows_mf/`: Windows Media Foundation implementation (stub)
- `crates/unienc/`: Main crate that conditionally compiles platform-specific implementations and exposes C FFI functions for the Unity plugin. `csbindgen` generates the C# bindings for the functions.

Key traits:
- `EncodingSystem`: Factory for creating encoders and muxers
- `Encoder`: Handles encoding of raw samples to compressed format
- `Muxer`: Combines encoded audio/video streams into container format
