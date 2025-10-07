## [1.1.0] - 2025/10/07

### Added

- Added `InstantReplay.Cri.CriAudioSampleProvider` to capture audio from CRIWARE.

### Changed

- Reduced heap allocations by frames.


## [1.0.3] - 2025/09/24

### Added

- Added pausing and resuming support for real-time recording.

### Fixed

- Fixed encoder error and memory leak leading the app to crash after entering background on iOS.

### Changed

- Added `/Brepro` option to compilation of `libunienc` for Windows to suppress unnecessary binary changes.

## [1.0.2] - 2025/09/12

### Fixed

- Added fallback to encoder (MFT) selection code for Windows to work with environments which fail to initialize the encoder with MF_E_UNSUPPORTED_D3D_TYPE (0xC00D6D76).
- Added context to error messages to improve debuggability.
- Changed UniEnc.VideoEncoder UniEnc.AudioEncoder to throw ObjectDisposedException instead of InvalidOperationException when the encoder input or output is disposed already.

## [1.0.1] - 2025/09/10

### Fixed

- Fixed the encoder tries to use unsupported hardware encoder of FFmpeg on Linux.

## [1.0.0] - 2025/09/09

### Added

- Added Linux x86_64 support for `unienc` depending on `ffmpeg` in `PATH`.

### Changed

- Real-time mode is now default.
- Former default mode is deprecated and renamed to legacy mode.
- "User Interfaces" sample now uses real-time mode.
- Removed legacy `ITranscoder` implementations for iOS, macOS, Android and Windows including native plugins and are replaced with the implementations used in real-time mode.
- `unienc` is now built with MSVC for Windows.
- `BoundedEncodedFrameBuffer`, `RealtimeFrameReadback`, `RealtimeRecorder` and `RealtimeTranscoder` are now internal.

### Fixed

- Improved stability of UniEnc on domain unloading during async operations are performed.

## [0.4.0] - 2025/08/22

### Added

- Added realtime encoding support for iOS, Android, macOS and Windows.

## [0.3.0] - 2025/06/17

### Added
- Added fallback transcoder using FFmpeg installed to `PATH` of the system for platforms other than iOS, Android, macOS, and Windows.

## [0.2.2] - 2025/06/09

### Breaking Changes

- `SrpScreenshotFrameProvider` and `BrpScreenshotFrameProvider` are removed and unified into `ScreenshotFrameProvider`.

### Fixed

- Fixed contents of other editor windows are captured on the editor.

## [0.2.1] - 2025/06/06

### Fixed

- Fixed `OldIosWorkaroundPostProcessor` fails to be compiled when there is no `UnityEditor.iOS.Xcode`.

## [0.2.0] - 2025/06/03

### Breaking Changes

- Signature of `IFrameProvider.OnFrameProvided` has been changed.

### Added

- Added `maxDuration` parameter to `InstantReplaySession.StopAndTranscodeAsync()`, allowing you to shorten the recording duration when transcoding.
- Added Built-in Render Pipeline support.

### Changed

- Changed instruction to install the package via `release` branch.
- Changed minimum macOS version from 14.3 to 10.15 (Catalina).

## [0.1.2] - 2025/05/26

### Fixed

- Fixed old iOS devices (< 16.0) crash on startup. The minimum iOS version is now 13.0.

## [0.1.1] - 2025/05/19

### Added

- Added "User Interfaces" sample

## [0.1.0] - 2025/04/14

### Added

- First release
