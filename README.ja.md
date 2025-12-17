# Instant Replay for Unity

[![](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![](https://img.shields.io/badge/PR-welcome-green.svg)](https://github.com/CyberAgentGameEntertainment/InstantReplay/pulls)
[![](https://img.shields.io/badge/Unity-2022.3-green.svg)](#インストール)

[English](README.md)

Instant Replay は Unity で直近のゲームプレイ動画をいつでも保存できるようにするライブラリです。  
必要なタイミングで直近のゲーム映像を遡って保存できるため、記録したい出来事を逃さずに録画できます。録画時間は事前に指定した長さを上限とし、上限を超えたフレームは破棄されます。

#### SNS へのゲームプレイ共有機能として

ユーザーが直近のゲームプレイ映像を SNS に共有する機能を実装することができます。

#### 不具合の再現手順の記録として

不具合が発生した際、直前に行った操作を動画として書き出すことで、不具合の再現等に役立てることができます。

## Table of Contents

<!-- TOC -->
* [Instant Replay for Unity](#instant-replay-for-unity)
  * [Table of Contents](#table-of-contents)
  * [要件](#要件)
    * [使用されるエンコーダー API](#使用されるエンコーダー-api)
  * [インストール](#インストール)
    * [依存関係のインストール](#依存関係のインストール)
      * [方法1: UnityNuGet と依存パッケージを使用したインストール](#方法1-unitynuget-と依存パッケージを使用したインストール)
      * [方法2: 手動でのインストール](#方法2-手動でのインストール)
    * [パッケージのインストール](#パッケージのインストール)
  * [クイックスタート](#クイックスタート)
  * [詳細な使い方](#詳細な使い方)
    * [設定](#設定)
    * [ポーズと再開](#ポーズと再開)
    * [映像ソースの設定](#映像ソースの設定)
    * [音声ソースの設定](#音声ソースの設定)
    * [録画状態を取得する](#録画状態を取得する)
  * [CRI サポート](#cri-サポート)
  * [無制限録画](#無制限録画)
  * [レガシーモード](#レガシーモード)
    * [録画時間とフレームレートの設定](#録画時間とフレームレートの設定)
    * [サイズの設定](#サイズの設定)
    * [映像・音声ソースの設定](#映像音声ソースの設定)
<!-- TOC -->

## 要件

- Unity 2022.3 以降

> [!NOTE]
> 以下の情報は使用している API やプラットフォームツール等から推定したもので、実際には動作が検証されていない場合があります。

Platform|OS version|aarch64|x86_64|Other requirements
-|-|-|-|-
iOS|10.0+|✅|N/A|
Android|8.0+|✅|✅|
macOS|11.0+|✅|✅|
Windows|Windows 10+, Windows Server 2016+|-|✅|
Linux|kernel 3.2+, glibc 2.17+|-|✅|`ffmpeg` in PATH

- レガシーモードでは、他のプラットフォームでも `ffmpeg` が PATH に存在すれば動作する可能性があります。

### 使用されるエンコーダー API

Platform|APIs
-|-
iOS / macOS|Video Toolbox (H.264), Audio Toolbox (AAC)
Android|MediaCodec (H.264 / AAC)
Windows|Media Foundation (H.264 / AAC)
Linux and others|システムにインストールされたFFmpeg (H.264 / AAC)

## インストール

### 依存関係のインストール

#### 方法1: UnityNuGet と依存パッケージを使用したインストール

[UnityNuGet の scoped registry を追加して](https://github.com/xoofx/UnityNuGet#add-scope-registry-manifestjson)、以下の git URL を Package Manager に追加してください。

```
https://github.com/CyberAgentGameEntertainment/InstantReplay.git?path=/Packages/jp.co.cyberagent.instant-replay.dependencies#release
```

#### 方法2: 手動でのインストール

[NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) や [UnityNuGet](https://github.com/bdovaz/UnityNuGet) を使用して以下のパッケージをインストールします。

- [System.IO.Pipelines](https://www.nuget.org/packages/system.io.pipelines/)
- [System.Threading.Channels](https://www.nuget.org/packages/System.Threading.Channels)

### パッケージのインストール

以下の git URL を Package Manager に追加してください。

```
https://github.com/CyberAgentGameEntertainment/InstantReplay.git?path=Packages/jp.co.cyberagent.instant-replay#release
```

## クイックスタート

Package Manager から "User Interfaces" サンプルをインポートしてください。

<img width="913" alt="Image" src="https://github.com/user-attachments/assets/970ad1e3-a5cf-410c-a2cb-70e0004e88e2" />

シーンに `InstantReplay Recorder.prefab` を配置します。この Prefab には `RecorderInterface` と `PersistentRecorder` コンポーネントが付いており、有効な間は自動的にゲームプレイを録画します。

<img width="585" alt="Image" src="https://github.com/user-attachments/assets/0724b264-f92b-4a68-b6dc-85b9aae9c05b" />

その後、`RecorderInterface.StopAndExport()` を呼び出すことで録画を停止してビデオを保存できます。例えば、シーン内のボタンをクリックすることでこのメソッドを呼び出すことができます。

<img width="585" alt="Image" src="https://github.com/user-attachments/assets/0674da6c-e7e8-4988-8890-01baa11f4322" />

録画したビデオが画面に表示されます。

![image](https://github.com/user-attachments/assets/f147e50d-a3e8-4dda-bfa3-22c1240f2904)

## 詳細な使い方

録画を行うには `RealtimeInstantReplaySession` を使用します。

```csharp
using InstantReplay;

var ct = destroyCancellationToken;

// 録画開始
using var session = RealtimeInstantReplaySession.CreateDefault();

// 〜 ゲームプレイ 〜
await Task.Delay(10000, ct);

// 録画停止と書き出し
var outputPath = await session.StopAndExportAsync();
File.Move(outputPath, Path.Combine(Application.persistentDataPath, Path.GetFileName(outputPath)));
```

### 設定

録画できる時間はメモリ使用量によって決定されます。デフォルト設定では 20MiB に設定されており、圧縮されたフレームや音声サンプルの合計サイズがこの上限に達すると古いデータから順に破棄されます。より長時間の録画を可能にするには、メモリ使用量 `MaxMemoryUsageBytesForCompressedFrames` を上げたり、フレームレートや解像度、ビットレートを下げてください。

実行時に使用されるメモリとしては、上記のエンコード済みのデータを保持するバッファに加え、エンコード前の生のフレームや音声サンプルがいくつか保持されます。これはエンコーダーが非同期的に動作する関係で、あるフレームをエンコードしている間に次のフレームを受け取るためです。`VideoInputQueueSize` と `AudioInputQueueSizeSeconds` でそれぞれのキューのサイズを指定できるほか、`MaxNumberOfRawFrameBuffers` (オプション) で圧縮前のフレームを保持するバッファの最大数を指定できます。この値を小さくすることでメモリ使用量を削減できる場合がありますが、フレームドロップの可能性が高まります。

```csharp
// デフォルト設定
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
    MaxNumberOfRawFrameBuffers = 2, // (オプション) エンコード前のフレームに使用されるバッファの最大数。各バッファは VideoOptions.Width * VideoOptions.Height * 4 バイトのサイズを持ちます。
    MaxMemoryUsageBytesForCompressedFrames = 20 * 1024 * 1024, // 20 MiB
    FixedFrameRate = 30.0, // 固定フレームレートを使用しない場合はnull
    VideoInputQueueSize = 5, // エンコード前の生のフレームを保持する数の上限
    AudioInputQueueSizeSeconds = 1.0 // エンコード前にバッファリングされる最大音声入力時間（秒）
};

using var session = new RealtimeInstantReplaySession(options);
```

### ポーズと再開

`RealtimeInstantReplaySession.Pause()` と `RealtimeInstantReplaySession.Resume()` を使用して録画を一時停止および再開できます。

### 映像ソースの設定

デフォルトでは `ScreenCapture.CaptureScreenshotIntoRenderTexture()` を使用して録画を行いますが、任意の RenderTexture をソースとして使用することも可能です。

`InstantReplay.IFrameProvider` を継承したクラスを作成し、`RealtimeInstantReplaySession` のコンストラクタに`frameProvider` として渡してください。また `disposeFrameProvider` によって `RealtimeInstantReplaySession` 側で `frameProvider` を自動的に破棄するかどうかを指定できます。

```csharp
public interface IFrameProvider : IDisposable
{
    public delegate void ProvideFrame(Frame frame);

    event ProvideFrame OnFrameProvided;
}

new RealtimeInstantReplaySession(options, frameProvider: new CustomFrameProvider(), disposeFrameProvider: true);

```

### 音声ソースの設定

デフォルトでは Unity デフォルトの出力音声を `OnAudioFilterRead` を使用してキャプチャします。これはシーン上の特定の AudioListener を自動的に検索して使用します。

> [!WARNING]
> Bypass Listener Effects が有効化された AudioSource の音声はキャプチャされません。

シーン上に複数の AudioListener が存在する場合は、`InstantReplay.UnityAudioSampleProvider` のコンストラクタに AudioListener を渡して初期化し、`RealtimeInstantReplaySession` のコンストラクタに `audioSampleProvider` として渡してください。

```csharp
new RealtimeInstantReplaySession(options, audioSampleProvider: new UnityAudioSampleProvider(audioListener), disposeAudioSampleProvider: true);
```

音声ソースを無効化したい場合は、`NullAudioSampleProvider.Instance` を使用してください。

```csharp
new RealtimeInstantReplaySession(options, audioSampleProvider: NullAudioSampleProvider.Instance);
```

> [!NOTE]
> `NullAudioSampleProvider` では `IDisposable` に関する考慮は不要です。

また、`IAudioSampleProvider` を実装することで独自の音声ソースを使用することも可能です。

```csharp
public interface IAudioSampleProvider : IDisposable
{
    public delegate void ProvideAudioSamples(ReadOnlySpan<float> samples, int channels, int sampleRate,
        double timestamp);

    event ProvideAudioSamples OnProvideAudioSamples;
}

new RealtimeInstantReplaySession(options, audioSampleProvider: new CustomAudioSampleProvider(), disposeFrameProvider: true);

```

### 録画状態を取得する

`InstantReplaySession.State` プロパティで録画の状態を取得できます。

## CRI サポート

InstantReplay は [CRIWARE](https://game.criware.jp/) からの音声をキャプチャするための `IAudioSampleProvider` 実装を提供しています。

1. CRIWARE Unity Plug-in をインストールします。
2. Player Settings でシンボル `INSTANTREPLAY_CRI` を追加します。
3. 必要な場合は `InstantReplay.Cri` アセンブリ参照を追加します。
4. `RealtimeInstantReplaySession` コンストラクタの `audioSampleProvider` に `InstantReplay.Cri.CriAudioSampleProvider` を指定します。

## 無制限録画

`UnboundedRecordingSession` を使用すると、エンコードしたデータをメモリに保持せず直接ディスク上の MP4 ファイルに書き出します。古いデータは破棄されず、制限なく追記されるため、より長時間の録画を行いやすくなります。コンストラクタで出力ファイルパスの指定が必要な以外は `RealtimeInstantReplaySession` と同様に使用できます。

> [!WARNING]
> 録画中にアプリがバックグラウンドに移行すると録画が停止し、録画ファイルが破損する可能性があります。バックグラウンド移行時には録画を一旦完了させることを推奨します。

```csharp
using InstantReplay;

var ct = destroyCancellationToken;

// 録画開始
using var session = new UnboundedRecordingSession("out.mp4", RealtimeEncodingOptions.Default);

// 〜 ゲームプレイ 〜
await Task.Delay(10000, ct);

// 録画停止と書き出し
await session.CompleteAsync();
```

## レガシーモード

デフォルトでは、`RealtimeInstantReplaySession` はビデオ・オーディオデータをリアルタイムでエンコードしますが、`InstantReplaySession` を使用するとJPEGで圧縮されたフレームとPCM音声サンプルを一時的にディスクに保存し、`StopAndTranscodeAsync()` 時にまとめてエンコードするレガシーモードで録画できます。ディスク負荷が大きい代わりに、録画中の計算負荷が小さくなります。

```csharp
using InstantReplay;

var ct = destroyCancellationToken;

// 録画開始
using var session = new InstantReplaySession(numFrames: 900, fixedFrameRate: 30);

// 〜 ゲームプレイ 〜
await Task.Delay(10000, ct);

// 録画停止と書き出し
var outputPath = await session.StopAndTranscodeAsync(ct: ct);
File.Move(outputPath, Path.Combine(Application.persistentDataPath, Path.GetFileName(outputPath)));
```

### 録画時間とフレームレートの設定

`InstantReplaySession` のコンストラクタでは `numFrames` と `fixedFrameRate` を指定できます。

```csharp
new InstantReplaySession(numFrames: 900, fixedFrameRate: 30);
 ```

`fixedFrameRate` を `null` に設定した場合、実際のFPSが使用されます。  
`numFrames` を超えたフレームは古いものから破棄されます。`numFrames` に比例して録画中のディスク使用量が大きくなるので、適度なサイズに設定してください。

### サイズの設定

デフォルトでは実際の画面サイズで録画しますが、`InstantReplaySession` のコンストラクタで `maxWidth` や `maxHeight` を指定することもできます。`maxWidth` や `maxHeight` を指定している場合は自動的にリサイズします。サイズを縮小することで録画中のディスク使用量や書き出しにかかる時間を短縮できます。また、録画中のメモリ使用量も減少します。

### 映像・音声ソースの設定

`InstantReplaySession` も `RealtimeInstantReplaySession` と同様に、`IFrameProvider` や `IAudioSampleProvider` を使用して映像・音声ソースをカスタマイズできます。
