# Instant Replay for Unity

[![](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![](https://img.shields.io/badge/PR-welcome-green.svg)](https://github.com/CyberAgentGameEntertainment/InstantReplay/pulls)
[![](https://img.shields.io/badge/Unity-2022.3-green.svg)](#installation)

[日本語](README.ja.md)

Instant Replay is a Unity library for saving recent gameplay footage on demand.
It maintains a rolling buffer, so you can save important moments even after they happen. Recording is limited to a preconfigured duration, and frames older than that limit are discarded.

### For Sharing Gameplay on Social Media

You can implement a feature that allows users to share their recent gameplay footage on social media.

### For Recording Bug Reproduction Steps

When a bug occurs, you can export the recent gameplay leading up to the bug as a video, which can help reproduce it.

## Table of Contents

<!-- TOC -->
* [Instant Replay for Unity](#instant-replay-for-unity)
  * [Table of Contents](#table-of-contents)
  * [Requirements](#requirements)
    * [Encoder APIs in use](#encoder-apis-in-use)
  * [Installation](#installation)
    * [Install Dependencies](#install-dependencies)
      * [Method 1: Install via UnityNuGet and dependency package](#method-1-install-via-unitynuget-and-dependency-package)
      * [Method 2: Install manually](#method-2-install-manually)
    * [Install the Package](#install-the-package)
  * [Quick Start](#quick-start)
  * [Detailed Usage](#detailed-usage)
    * [Options](#options)
    * [Pausing and Resuming](#pausing-and-resuming)
    * [Setting the Video Source](#setting-the-video-source)
      * [Built-in `IFrameProvider`](#built-in-iframeprovider)
      * [Custom `IFrameProvider` Implementation](#custom-iframeprovider-implementation)
    * [Setting the Audio Source](#setting-the-audio-source)
      * [CRI Support](#cri-support)
      * [Wwise Support](#wwise-support)
    * [Getting the Recording State](#getting-the-recording-state)
  * [Disk Buffering and Crash Recovery](#disk-buffering-and-crash-recovery)
    * [Recovering After a Crash](#recovering-after-a-crash)
    * [Disk Buffer Options](#disk-buffer-options)
  * [Unbounded Recording](#unbounded-recording)
  * [Legacy Mode](#legacy-mode)
    * [Setting Recording Time and Frame Rate](#setting-recording-time-and-frame-rate)
    * [Setting the Size](#setting-the-size)
    * [Video and Audio Sources](#video-and-audio-sources)
  * [Excluding from the Release Builds](#excluding-from-the-release-builds)
  * [License](#license)
<!-- TOC -->

## Requirements

- Unity 2022.3 or later

> [!NOTE]
> The following information is based on the APIs and platform tools being used, and actual functionality may not have been verified.

Platform|OS version|aarch64|x86_64|Other requirements
-|-|-|-|-
iOS|10.0+|✅|N/A|
Android|8.0+|✅|✅|
macOS|11.0+|✅|✅|
Windows|Windows 10+, Windows Server 2016+|-|✅|
Linux|kernel 3.2+, glibc 2.17+|-|✅|`ffmpeg` in PATH
Web|(any)|(any)|(any)|[Browser supports WebCodecs](#encoder-apis-in-use)

- For legacy mode, other platforms may work if `ffmpeg` is available in PATH.

>[!WARNING]
> **Known Issue with WebGL**: In WebGL, flickering may occur on the screen during recording. This is caused by `ScreenshotFrameProvider`, which is the default `IFrameProvider` implementation. If you encounter this issue, please use [`BuiltinCameraFrameProvider`](#built-in-iframeprovider) (for Built-in RP), [`RendererFeatureFrameProvider`](#built-in-iframeprovider) (for Universal RP), or another custom `IFrameProvider` implementation that provides input `RenderTexture` directly.

### Encoder APIs in use

Platform|APIs
-|-
iOS / macOS|Video Toolbox (H.264), Audio Toolbox (AAC)
Android|MediaCodec (H.264 / AAC)
Windows|Media Foundation (H.264 / AAC)
Linux|FFmpeg installed on the system (H.264 / AAC)
Web|[WebCodecs](https://caniuse.com/webcodecs) (`avc1.640028` for video, `mp4a.40.2` for audio)

## Installation

### Install Dependencies

You can install the dependencies using either of the following methods.

#### Method 1: Install via UnityNuGet and dependency package

[Add UnityNuGet scoped registry](https://github.com/xoofx/UnityNuGet#add-scope-registry-manifestjson) and add the following git URL to the Package Manager:

```
https://github.com/CyberAgentGameEntertainment/InstantReplay.git?path=/Packages/jp.co.cyberagent.instant-replay.dependencies#release
```

#### Method 2: Install manually

Install individual packages using [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) or [UnityNuGet](https://github.com/bdovaz/UnityNuGet):

- [System.IO.Pipelines](https://www.nuget.org/packages/system.io.pipelines/)
- [System.Threading.Channels](https://www.nuget.org/packages/System.Threading.Channels)

### Install the Package

Add the following git URL to the Package Manager:

```
https://github.com/CyberAgentGameEntertainment/InstantReplay.git?path=Packages/jp.co.cyberagent.instant-replay#release
```

## Quick Start

Import the "User Interfaces" sample from the Package Manager.

<img width="913" alt="Image" src="https://github.com/user-attachments/assets/970ad1e3-a5cf-410c-a2cb-70e0004e88e2" />

Place `InstantReplay Recorder.prefab` in the scene. This prefab contains `RecorderInterface` and `PersistentRecorder` components, which automatically record the gameplay while enabled.

<img width="585" alt="Image" src="https://github.com/user-attachments/assets/0724b264-f92b-4a68-b6dc-85b9aae9c05b" />

Then, you can stop the recording and save the video by calling `RecorderInterface.StopAndExport()`. For example, you can trigger this method by clicking the button in the scene.

<img width="585" alt="Image" src="https://github.com/user-attachments/assets/0674da6c-e7e8-4988-8890-01baa11f4322" />

The recorded video will play on the screen.

![image](https://github.com/user-attachments/assets/f147e50d-a3e8-4dda-bfa3-22c1240f2904)

## Detailed Usage

To record gameplay from code, use `RealtimeInstantReplaySession` as shown below.

```csharp
using InstantReplay;

var ct = destroyCancellationToken;

// Start recording
using var session = RealtimeInstantReplaySession.CreateDefault();

// 〜 Gameplay 〜
await Task.Delay(10000, ct);

// Stop recording and transcode
var outputPath = await session.StopAndExportAsync();
File.Move(outputPath, Path.Combine(Application.persistentDataPath, Path.GetFileName(outputPath)));
```

### Options

Recording uses memory in two places: buffers for compressed output, and buffers for raw frames and audio samples awaiting encoding.

`MaxMemoryUsageBytesForCompressedFrames` controls the recording duration. By default, recording holds up to 20 MiB of compressed data; when the total size of compressed frames and audio samples reaches this limit, older data is discarded. To enable longer recordings, increase this value or reduce the frame rate, resolution, or bitrate.

`VideoInputQueueSize`, `AudioInputQueueSizeSeconds`, and `MaxNumberOfRawFrameBuffers` (optional) control how many raw frames and audio samples are queued for encoding. These queues are needed because the encoder runs asynchronously, receiving the next frame while encoding the current one. Reducing these values decreases memory usage but may increase the likelihood of dropped frames.

```csharp
// Default settings
var options = new RealtimeEncodingOptions
{
    VideoOptions = new VideoEncoderOptions
    {
        Width = 1280,
        Height = 720,
        FpsHint = 30,
        Bitrate = 2500000 // 2.5 Mbps
    },
    AudioOptions = new AudioEncoderOptions
    {
        SampleRate = 44100,
        Channels = 2,
        Bitrate = 128000 // 128 kbps
    },
    MaxNumberOfRawFrameBuffers = 2, // (Optional) Max number of buffers to store frames to be encoded. Each buffer size is VideoOptions.Width * VideoOptions.Height * 4 bytes.
    MaxMemoryUsageBytesForCompressedFrames = 20 * 1024 * 1024, // 20 MiB
    FixedFrameRate = 30.0, // null to use the actual rendering frame rate
    VideoInputQueueSize = 5, // Maximum number of raw frames to keep before encoding
    AudioInputQueueSizeSeconds = 1.0 // Max queued audio input duration to be buffered before encoding, in seconds
};

using var session = new RealtimeInstantReplaySession(options);
```

### Pausing and Resuming

You can pause and resume the recording using `RealtimeInstantReplaySession.Pause()` and `RealtimeInstantReplaySession.Resume()`.

### Setting the Video Source

You can use a custom video source by implementing `IFrameProvider`.

Pass an `IFrameProvider` instance as `frameProvider` to the `RealtimeInstantReplaySession` constructor. You can also specify whether `RealtimeInstantReplaySession` automatically disposes `frameProvider` via the `disposeFrameProvider` parameter.

```csharp

new RealtimeInstantReplaySession(options, frameProvider: new ScreenshotFrameProvider(), disposeFrameProvider: true);

```

#### Built-in `IFrameProvider`

- `ScreenshotFrameProvider`: This is the default `IFrameProvider` implementation. It uses `ScreenCapture.CaptureScreenshotIntoRenderTexture()`, which allows it to capture the entire screen, including overlay canvases that are not rendered by a specific camera. However, it increases GPU memory usage due to the additional RenderTexture used for capturing.
- `BuiltinCameraFrameProvider`: Captures the footage of a specific camera using `OnRenderImage()` in Built-in Render Pipeline.
- `RendererFeatureFrameProvider`: Captures the footage of a specific camera using Renderer Feature in Universal Render Pipeline. You need to add `InstantReplayFrameRendererFeature` to the Renderer used by the camera.

#### Custom `IFrameProvider` Implementation

Create a class that inherits `InstantReplay.IFrameProvider`.

```csharp
public interface IFrameProvider : IDisposable
{
    public delegate void ProvideFrame(Frame frame);

    event ProvideFrame OnFrameProvided;
}

new RealtimeInstantReplaySession(options, frameProvider: new CustomFrameProvider(), disposeFrameProvider: true);

```

### Setting the Audio Source

By default, `RealtimeInstantReplaySession` captures the audio via `OnAudioFilterRead`. This automatically searches for and uses a specific AudioListener in the scene.

> [!WARNING]
> AudioSources with Bypass Listener Effects enabled will not be captured.

If there are multiple AudioListeners in the scene, create a `UnityAudioSampleProvider` with the one you want to use. Then pass it to the `RealtimeInstantReplaySession` constructor via the `audioSampleProvider` parameter.

```csharp
new RealtimeInstantReplaySession(options, audioSampleProvider: new UnityAudioSampleProvider(audioListener), disposeAudioSampleProvider: true);
```

If you want to disable the audio, you can use `NullAudioSampleProvider.Instance`.

```csharp
new RealtimeInstantReplaySession(options, audioSampleProvider: NullAudioSampleProvider.Instance);
```

> [!NOTE]
> You don't need to dispose `NullAudioSampleProvider.Instance` because it is a shared singleton.

You can also use your own audio source by implementing `IAudioSampleProvider`.

```csharp
public interface IAudioSampleProvider : IDisposable
{
    public delegate void ProvideAudioSamples(ReadOnlySpan<float> samples, int channels, int sampleRate,
        double timestamp);

    event ProvideAudioSamples OnProvideAudioSamples;
}

new RealtimeInstantReplaySession(options, audioSampleProvider: new CustomAudioSampleProvider(), disposeAudioSampleProvider: true);

```

#### CRI Support

InstantReplay provides `CriAudioSampleProvider`, an `IAudioSampleProvider` implementation that captures audio from [CRIWARE](https://game.criware.jp/).

1. Install CRIWARE Unity Plug-in
2. Add scripting define symbol `INSTANTREPLAY_CRI` in Player Settings
3. Add `InstantReplay.Cri` assembly reference if necessary
4. Use `InstantReplay.Cri.CriAudioSampleProvider` as `audioSampleProvider` in `RealtimeInstantReplaySession` constructor

#### Wwise Support

Wwise is also supported via `InstantReplay.Wwise.WwiseAudioSampleProvider`.

1. Install Wwise Unity Integration
2. Add scripting define symbol `INSTANTREPLAY_WWISE` in Player Settings
3. Add `InstantReplay.Wwise` assembly reference if necessary
4. Use `InstantReplay.Wwise.WwiseAudioSampleProvider` as `audioSampleProvider` in `RealtimeInstantReplaySession` constructor

### Getting the Recording State

You can get the recording state with the `RealtimeInstantReplaySession.State` property.

## Disk Buffering and Crash Recovery

By default, `RealtimeInstantReplaySession` holds encoded frames in memory. Assigning `RealtimeEncodingOptions.DiskBuffer` writes them to segment files on disk instead. This lowers memory pressure, and it leaves the footage that preceded an abnormal termination on disk, where a later run of the application can recover it.

> [!WARNING]
> Recording continuously to storage shortens the lifespan of flash memory. This is intended primarily for development and quality-assurance builds rather than for builds shipped to end users.

```csharp
using InstantReplay;

var options = RealtimeEncodingOptions.Default;
options.DiskBuffer = DiskBufferOptions.Default;

using var session = new RealtimeInstantReplaySession(options);
```

`MaxMemoryUsageBytesForCompressedFrames` is not used while the disk buffer is enabled. `DiskBufferOptions.MaxDiskUsageBytes` bounds the retained data instead.

### Recovering After a Crash

Each session writes into its own directory. A session that is disposed normally deletes its directory unless `RetainOnDispose` is set, so a directory still present on the next run denotes a session that did not end normally.

```csharp
using InstantReplay;

foreach (var recovery in DiskEncodedFrameBufferRecovery.FindRecoverable())
{
    if (!recovery.IsCompatible) continue;

    var path = await recovery.ExportAsync(durationSeconds: 30);
    Debug.Log($"Recovered {path} (started at {recovery.StartedAtUtc:u}, {recovery.SizeBytes} bytes)");

    recovery.Delete();
}
```

`FindRecoverable` enumerates every recoverable session below the given root directory, or below the directory the recorder uses by default when none is passed. Several may be present when the application has terminated abnormally more than once, and the caller decides which to export and which to discard. `TryGetRecoverable` reads a single session directory when its path is already known.

Recovery never deletes anything implicitly, because a session left behind by a crash is the only copy of the footage that preceded it. Call `Delete()` once the exported file has been dealt with.

`IsCompatible` compares the platform and package version recorded in the manifest against the running application. It is necessary but not sufficient: the payloads are serialized by the native library, whose schema may change between package versions, and a mismatch of that kind surfaces as a failure from `ExportAsync` instead.

### Disk Buffer Options

| Property | Default | Description |
|---|---|---|
| `Directory` | `null` | Directory holding the session directories. `null` or empty uses `Application.temporaryCachePath/InstantReplay/DiskBuffer`, available as `DiskBufferOptions.GetDefaultDirectory()`. |
| `MaxDiskUsageBytes` | 256 MiB | Upper bound of one session directory, covering the manifest, the codec configuration, and every segment. Must be at least `DiskBufferOptions.MinimumDiskUsageBytes` (4 MiB). |
| `SegmentDuration` | 5.0 | Target duration of one segment file, in seconds. |
| `MaxSegmentBytes` | 8 MiB | Upper bound of one segment file. Must not exceed `MaxDiskUsageBytes`. |
| `MaxPendingWriteBytes` | 4 MiB | Upper bound of the payload waiting in the write queue. Frames arriving while it is full are dropped rather than blocking the encoder. |
| `RetainOnDispose` | `false` | Whether the session directory is kept when the session is disposed normally. |
| `SyncMode` | `OperatingSystem` | Flush policy. See below. |

`MaxDiskUsageBytes` is a bound rather than a target. Space is reserved before each record is written, and the reservation deletes as many of the oldest segments as it needs, so the directory never exceeds the bound at any instant. When the bound cannot be met even after every evictable segment has been deleted, records are dropped rather than written; a value close to `MaxSegmentBytes` therefore degrades the recording rather than the retention. Segments are closed at video key frames, so discarding one never leaves a partial group of pictures behind.

`SyncMode` selects which failure mode to defend against. `DiskBufferSyncMode.OperatingSystem`, the default, hands data to the operating system after every batch and flushes it to the storage device when a segment is closed. Recorded frames survive a process crash — a native fault, an out-of-memory kill, or an abort — which is what crash recovery targets, and this costs no additional device writes. Power loss or a kernel panic loses at most the records written since the current segment was opened. `DiskBufferSyncMode.EveryRecord` flushes every record to the device, which survives power loss as well, at the cost of one device flush per frame. It markedly increases wear on flash memory and is intended for diagnosing storage-layer problems rather than for routine use.

## Unbounded Recording

`UnboundedRecordingSession` writes encoded data directly to an MP4 file on disk without keeping it in memory, enabling unbounded recording limited only by available disk space. Apart from the required output file path in the constructor, `UnboundedRecordingSession` is used similarly to `RealtimeInstantReplaySession`.

> [!WARNING]
> If the app goes to the background during recording, the recording may stop and the recorded file may become corrupted. It is recommended to complete the recording before the app goes to the background.

```csharp
using InstantReplay;

var ct = destroyCancellationToken;

// Start recording
using var session = new UnboundedRecordingSession("out.mp4", RealtimeEncodingOptions.Default);

// 〜 Gameplay 〜
await Task.Delay(10000, ct);

// Stop recording and export
await session.CompleteAsync();
```

## Legacy Mode

By default, `RealtimeInstantReplaySession` encodes video and audio samples in real-time, but legacy `InstantReplaySession` saves JPEG-compressed video frames and raw audio samples to disk and transcodes them to a video file when `StopAndTranscodeAsync` is called. While this mode has higher disk I/O, it reduces the CPU load during recording.

```csharp
using InstantReplay;

var ct = destroyCancellationToken;

// Start recording
using var session = new InstantReplaySession(numFrames: 900, fixedFrameRate: 30);

// 〜 Gameplay 〜
await Task.Delay(10000, ct);

// Stop recording and transcode
var outputPath = await session.StopAndTranscodeAsync(ct: ct);
File.Move(outputPath, Path.Combine(Application.persistentDataPath, Path.GetFileName(outputPath)));
```

### Setting Recording Time and Frame Rate

You can specify `numFrames` and `fixedFrameRate` in the `InstantReplaySession` constructor.

```csharp
new InstantReplaySession(numFrames: 900, fixedFrameRate: 30);
 ```

If you set `fixedFrameRate` to `null`, the actual frame rate will be used.
When the number of frames exceeds `numFrames`, the oldest frames are discarded. The disk usage during recording increases in proportion to `numFrames`, so set it to an appropriate size.

### Setting the Size

By default, recordings use the actual screen size. You can cap the output resolution with `maxWidth` and `maxHeight` in the `InstantReplaySession` constructor. Reducing the size lowers disk usage, write time, and memory usage during recording.

### Video and Audio Sources

`InstantReplaySession` also supports custom video and audio sources in the same way as `RealtimeInstantReplaySession`.

## Excluding from the Release Builds

If you are using **InstantReplay** as part of your bug reporting workflow, you should exclude script and plugin files in your release builds.

You can exclude all library scripts by adding `EXCLUDE_INSTANTREPLAY` to the Scripting Define Symbols in the Player Settings. To exclude your own code from the release builds, wrap it in `#if !EXCLUDE_INSTANTREPLAY`.

## License

[MIT](LICENSE)

For the licenses of the dependencies used, please refer to [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
