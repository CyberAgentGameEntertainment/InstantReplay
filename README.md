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
    * [Supported Platforms](#supported-platforms)
  * [Installation](#installation)
    * [Install Dependencies](#install-dependencies)
      * [Method 1: Install via UnityNuGet and dependency package](#method-1-install-via-unitynuget-and-dependency-package)
      * [Method 2: Install manually](#method-2-install-manually)
    * [Install the Package](#install-the-package)
  * [Quick Start](#quick-start)
  * [Detailed Usage](#detailed-usage)
    * [Setting Recording Time and Frame Rate](#setting-recording-time-and-frame-rate)
    * [Setting the Size](#setting-the-size)
    * [Setting the Video Source](#setting-the-video-source)
    * [Setting the Audio Source](#setting-the-audio-source)
    * [Getting the Recording State](#getting-the-recording-state)
    * [Getting the Progress of Writing](#getting-the-progress-of-writing)
<!-- TOC -->

## Requirements

- Unity 2022.3 or later

### Supported Platforms

- iOS
- Android
- macOS (Editor and Standalone)
- Windows (Editor and Standalone)

## Installation

### Install Dependencies

There are two ways to install the dependencies. You can use either of them.

#### Method 1: Install via UnityNuGet and dependency package

[Add UnityNuGet scoped registry](https://github.com/xoofx/UnityNuGet#add-scope-registry-manifestjson) and add the following git URL to the package manager:

```
https://github.com/CyberAgentGameEntertainment/InstantReplay.git?path=/Packages/jp.co.cyberagent.instant-replay.dependencies
```

#### Method 2: Install manually

Install individual packages using [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) or [UnityNuGet](https://github.com/bdovaz/UnityNuGet):

- [System.IO.Pipelines](https://www.nuget.org/packages/system.io.pipelines/)
- [System.Threading.Channels](https://www.nuget.org/packages/System.Threading.Channels)

### Install the Package

Add the following git URL to the package manager:

```
https://github.com/CyberAgentGameEntertainment/InstantReplay.git?path=Packages/jp.co.cyberagent.instant-replay
```

## Quick Start

Import "User Interfaces" sample from the package manager.

<img width="913" alt="Image" src="https://github.com/user-attachments/assets/970ad1e3-a5cf-410c-a2cb-70e0004e88e2" />

Place `InstantReplay Recorder.prefab` in the scene. This prefab has `Recorder` component, which will automatically record the gameplay during enabled.

<img width="672" alt="Image" src="https://github.com/user-attachments/assets/2dd76940-3b68-40c4-86a4-984133439135" />

Then, you can stop the recording and save the video by calling `Recorder.StopAndTranscode()`. For example, you can trigger this method by clicking the button in the scene.

<img width="618" alt="Image" src="https://github.com/user-attachments/assets/0a14a29d-7d95-49d4-bec9-205bc48feee9" />

Output filename will be displayed on the screen.

![Image](https://github.com/user-attachments/assets/a19e7aca-78cb-4df3-96b9-cc3c05e8b006)

## Detailed Usage

To record the gameplay, use `InstantReplaySession`.

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

### Setting the Video Source

By default, it uses `RenderPipelineManager.endContextRendering` and `ScreenCapture.CaptureScreenshotIntoRenderTexture` provided for SRP to capture the screen, but you can also use any RenderTexture as the source.

Create a class that inherits `InstantReplay.IFrameProvider` and pass it as `frameProvider` to the `InstantReplaySession` constructor. You can also specify whether `InstantReplaySession` automatically discards `frameProvider` by `disposeFrameProvider`.

```csharp
public interface IFrameProvider : IDisposable
{
    public delegate void ProvideFrame(RenderTexture frame, double timestamp);

    event ProvideFrame OnFrameProvided;
}

new InstantReplaySession(900, frameProvider: new CustomFrameProvider(), disposeFrameProvider: true);

```

### Setting the Audio Source

By default, it captures the audio via `OnAudioFilterRead`. THis automatically searches for and uses a specific AudioListener on the scene.

> [!WARNING]
> AudioSource with Bypass Listener Effects will not be captured.

If there are multiple AudioListeners in the scene, you can specify which one to use by passing it to the `InstantReplay.UnityAudioSampleProvider` constructor and then passing it as `audioSampleProvider` to the `InstantReplaySession` constructor.

```csharp
new InstantReplaySession(900, audioSampleProvider: new UnityAudioSampleProvider(audioListener), disposeAudioSampleProvider: true);
```

If you want to disable the audio, you can use `NullAudioSampleProvider.Instance`.

```csharp
new InstantReplaySession(900, audioSampleProvider: NullAudioSampleProvider.Instance);
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

new InstantReplaySession(900, audioSampleProvider: new CustomAudioSampleProvider(), disposeFrameProvider: true);

```

### Getting the Recording State

You can get the recording state with the `InstantReplaySession.State` property.

### Getting the Progress of Writing

You can get the progress in the range of 0.0 to 1.0 by passing `IProgress<float>` to `InstantReplaySession.StopAndTranscodeAsync`.
