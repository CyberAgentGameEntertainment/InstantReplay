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
