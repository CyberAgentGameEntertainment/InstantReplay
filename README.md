# Instant Replay for Unity

[![](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![](https://img.shields.io/badge/PR-welcome-green.svg)](https://github.com/CyberAgentGameEntertainment/InstantReplay/pulls)
[![](https://img.shields.io/badge/Unity-2022.3-green.svg)](#installation)

[日本語](README.ja.md)

Instant Replay is a library that allows you to save recent gameplay videos at any time in Unity.
You can save recent game footage retroactively when needed, ensuring you don't miss recording important moments. The recording time is limited to a pre-specified length, and frames exceeding this limit are discarded.

### For Sharing Gameplay on SNS

You can implement a feature that allows users to share their recent gameplay footage on social media.

### For Recording Reproduction Steps of Bugs

When a bug occurs, you can export the operations performed up to that point as a video, which can be useful for reproducing the bug.

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
    * [Setting the Audio Source](#setting-the-audio-source)
    * [Getting the Recording State](#getting-the-recording-state)
  * [CRI support](#cri-support)
  * [Unbounded Recording](#unbounded-recording)
  * [Legacy Mode](#legacy-mode)
    * [Setting Recording Time and Frame Rate](#setting-recording-time-and-frame-rate)
    * [Setting the Size](#setting-the-size)
    * [Video and Audio Sources](#video-and-audio-sources)
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

- For legacy mode, other platforms may work if `ffmpeg` is available in PATH.

### Encoder APIs in use

Platform|APIs
-|-
iOS / macOS|Video Toolbox (H.264), Audio Toolbox (AAC)
Android|MediaCodec (H.264 / AAC)
Windows|Media Foundation (H.264 / AAC)
Linux and others|FFmpeg installed on the system (H.264 / AAC)

## Installation

### Install Dependencies

There are two ways to install the dependencies. You can use either of them.

#### Method 1: Install via UnityNuGet and dependency package

[Add UnityNuGet scoped registry](https://github.com/xoofx/UnityNuGet#add-scope-registry-manifestjson) and add the following git URL to the package manager:

```
https://github.com/CyberAgentGameEntertainment/InstantReplay.git?path=/Packages/jp.co.cyberagent.instant-replay.dependencies#release
```

#### Method 2: Install manually

Install individual packages using [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) or [UnityNuGet](https://github.com/bdovaz/UnityNuGet):

- [System.IO.Pipelines](https://www.nuget.org/packages/system.io.pipelines/)
- [System.Threading.Channels](https://www.nuget.org/packages/System.Threading.Channels)

### Install the Package

Add the following git URL to the package manager:

```
https://github.com/CyberAgentGameEntertainment/InstantReplay.git?path=Packages/jp.co.cyberagent.instant-replay#release
```

## Quick Start

Import "User Interfaces" sample from the package manager.

<img width="913" alt="Image" src="https://github.com/user-attachments/assets/970ad1e3-a5cf-410c-a2cb-70e0004e88e2" />

Place `InstantReplay Recorder.prefab` in the scene. This prefab has `RecorderInterface` and `PersistentRecorder` component, which will automatically record the gameplay during enabled.

<img width="585" alt="Image" src="https://github.com/user-attachments/assets/0724b264-f92b-4a68-b6dc-85b9aae9c05b" />

Then, you can stop the recording and save the video by calling `RecorderInterface.StopAndExport()`. For example, you can trigger this method by clicking the button in the scene.

<img width="585" alt="Image" src="https://github.com/user-attachments/assets/0674da6c-e7e8-4988-8890-01baa11f4322" />

Recorded video will be displayed on the screen.

![image](https://github.com/user-attachments/assets/f147e50d-a3e8-4dda-bfa3-22c1240f2904)

## Detailed Usage

To record the gameplay, use `RealtimeInstantReplaySession`.

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

The recording duration is determined by the memory usage. The default setting is set to 20 MiB, and when the total size of compressed frames and audio samples reaches this limit, older data is discarded. To enable longer recordings, increase the memory usage `MaxMemoryUsageBytesForCompressedFrames` or reduce the frame rate, resolution, or bitrate.

It consumes some memory for the buffers that hold the compressed data, as well as for the raw frames and audio samples to be encoded. This is necessary because the encoder operates asynchronously, allowing it to receive the next frame while encoding the current one. You can specify the number of frames stored concurrently with `VideoInputQueueSize` and `AudioInputQueueSizeSeconds`, and the max number of raw frame buffers with `MaxNumberOfRawFrameBuffers` (optional). Reducing these values can decrease memory usage, but it may increase the likelihood of frame drops.

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
    FixedFrameRate = 30.0, // null if not using fixed frame rate
    VideoInputQueueSize = 5, // Maximum number of raw frames to keep before encoding
    AudioInputQueueSizeSeconds = 1.0 // Max queued audio input duration to be buffered before encoding, in seconds
};

using var session = new RealtimeInstantReplaySession(options);
```

### Pausing and Resuming

You can pause and resume the recording using `RealtimeInstantReplaySession.Pause()` and `RealtimeInstantReplaySession.Resume()`.

### Setting the Video Source

By default, InstantReplay uses `ScreenCapture.CaptureScreenshotIntoRenderTexture()` for recording. You can also use any RenderTexture as the source.

Create a class that inherits `InstantReplay.IFrameProvider` and pass it as `frameProvider` to the `RealtimeInstantReplaySession` constructor. You can also specify whether `RealtimeInstantReplaySession` automatically discards `frameProvider` by `disposeFrameProvider`.

```csharp
public interface IFrameProvider : IDisposable
{
    public delegate void ProvideFrame(Frame frame);

    event ProvideFrame OnFrameProvided;
}

new RealtimeInstantReplaySession(options, frameProvider: new CustomFrameProvider(), disposeFrameProvider: true);

```

### Setting the Audio Source

By default, it captures the audio via `OnAudioFilterRead`. THis automatically searches for and uses a specific AudioListener on the scene.

> [!WARNING]
> AudioSource with Bypass Listener Effects will not be captured.

If there are multiple AudioListeners in the scene, you can specify which one to use by passing it to the `InstantReplay.UnityAudioSampleProvider` constructor and then passing it as `audioSampleProvider` to the `RealtimeInstantReplaySession` constructor.

```csharp
new RealtimeInstantReplaySession(options, audioSampleProvider: new UnityAudioSampleProvider(audioListener), disposeAudioSampleProvider: true);
```

If you want to disable the audio, you can use `NullAudioSampleProvider.Instance`.

```csharp
new RealtimeInstantReplaySession(options, audioSampleProvider: NullAudioSampleProvider.Instance);
```

> [!NOTE]
> You don't have to care about `IDisposable` of `NullAudioSampleProvider`.

You can also use your own audio source by implementing `IAudioSampleProvider`.

```csharp
public interface IAudioSampleProvider : IDisposable
{
    public delegate void ProvideAudioSamples(ReadOnlySpan<float> samples, int channels, int sampleRate,
        double timestamp);

    event ProvideAudioSamples OnProvideAudioSamples;
}

new RealtimeInstantReplaySession(options, audioSampleProvider: new CustomAudioSampleProvider(), disposeFrameProvider: true);

```

### Getting the Recording State

You can get the recording state with the `InstantReplaySession.State` property.

## CRI support

InstantReplay provides the `IAudioSampleProvider` implementation to capture audio from [CRIWARE](https://game.criware.jp/).

1. Install CRIWARE Unity Plug-in
2. Add scripting define symbol `INSTANTREPLAY_CRI` in player settings
3. Add `InstantReplay.Cri` assembly reference if necessary
4. Use `InstantReplay.Cri.CriAudioSampleProvider` as `audioSampleProvider` in `RealtimeInstantReplaySession` constructor

## Unbounded Recording

By using `UnboundedRecordingSession`, you can write the encoded data directly to an MP4 file on disk without keeping it in memory. This allows for recording without time limits, as long as there is sufficient disk space. Other than specifying the output file path in the constructor, it can be used in the same way as `RealtimeInstantReplaySession`.

> [!WARNING]
> If the app goes to the background during recording, the recording may stop and the recorded file may become corrupted. It is recommended to complete the recording when transitioning to the background.

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

By default, `RealtimeInstantReplaySession` encodes video and audio samples in real-time but legacy `InstantReplaySession` saves JPEG-compressed video frames and raw audio samples into disk and transcodes them to a video file when `StopAndTranscodeAsync` is called. While this mode has a higher disk access, it reduces the CPU load during recording.

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

If you set `fixedFrameRate` to `null`, the actual FPS will be used.
Frames exceeding `numFrames` will be discarded from the oldest. The disk usage during recording increases in proportion to `numFrames`, so set it to an appropriate size.

### Setting the Size

By default, it records at the actual screen size, but you can also specify `maxWidth` and `maxHeight` in the `InstantReplaySession` constructor. If you specify `maxWidth` and `maxHeight`, it will automatically resize. Reducing the size can reduce the disk usage and time required for writing during recording. It also reduces memory usage during recording.

### Video and Audio Sources

`InstantReplaySession` also supports custom video and audio sources in the same way as `RealtimeInstantReplaySession`.

## Exclude from the release builds

If you are using **InstantReplay** as part of your bug collection, you should exclude script and plugin files in your release builds.

You can exclude all scripts of the **InstantReplay** by adding **EXCLUDE_INSTANTREPLAY** to the **Scripting Define Symbols** in the **Player Settings**.
Thus, if you enclose all your own code that accesses the **InstantReplay** with `#if EXCLUDE_INSTANTREPLAY`, you can exclude all the code from the release builds.