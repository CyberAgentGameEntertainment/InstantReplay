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
    * [対応プラットフォーム](#対応プラットフォーム)
  * [インストール](#インストール)
    * [依存関係のインストール](#依存関係のインストール)
      * [方法1: UnityNuGet と依存パッケージを使用したインストール](#方法1-unitynuget-と依存パッケージを使用したインストール)
      * [方法2: 手動でのインストール](#方法2-手動でのインストール)
    * [パッケージのインストール](#パッケージのインストール)
  * [クイックスタート](#クイックスタート)
  * [詳細な使い方](#詳細な使い方)
    * [録画時間とフレームレートの設定](#録画時間とフレームレートの設定)
    * [サイズの設定](#サイズの設定)
    * [映像ソースの設定](#映像ソースの設定)
    * [音声ソースの設定](#音声ソースの設定)
    * [録画状態を取得する](#録画状態を取得する)
    * [書き出しの進捗状況を取得する](#書き出しの進捗状況を取得する)
<!-- TOC -->

## 要件

- Unity 2022.3 以降

### 対応プラットフォーム

- iOS
  - **13.0以降**
- Android
- macOS (Editor and Standalone)
  - **10.15 (Catalina) 以降**
- Windows (Editor and Standalone)
- その他 `ffmpeg` コマンドラインツールが PATH にインストールされたシステム

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

その後、`RecorderInterface.StopAndTranscode()` を呼び出すことで録画を停止してビデオを保存できます。例えば、シーン内のボタンをクリックすることでこのメソッドを呼び出すことができます。

<img width="585" alt="Image" src="https://github.com/user-attachments/assets/0674da6c-e7e8-4988-8890-01baa11f4322" />

録画したビデオが画面に表示されます。

![image](https://github.com/user-attachments/assets/f147e50d-a3e8-4dda-bfa3-22c1240f2904)

## 詳細な使い方

録画を行うには `InstantReplaySession` を使用します。

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

### 映像ソースの設定

デフォルトでは `ScreenCapture.CaptureScreenshotIntoRenderTexture()` を使用して録画を行いますが、任意の RenderTexture をソースとして使用することも可能です。

`InstantReplay.IFrameProvider` を継承したクラスを作成し、`InstantReplaySession` のコンストラクタに`frameProvider` として渡してください。また `disposeFrameProvider` によって `InstantReplaySession` 側で `frameProvider` を自動的に破棄するかどうかを指定できます。

```csharp
public interface IFrameProvider : IDisposable
{
    public delegate void ProvideFrame(Frame frame);

    event ProvideFrame OnFrameProvided;
}

new InstantReplaySession(900, frameProvider: new CustomFrameProvider(), disposeFrameProvider: true);

```

### 音声ソースの設定

デフォルトでは Unity デフォルトの出力音声を `OnAudioFilterRead` を使用してキャプチャします。これはシーン上の特定の AudioListener を自動的に検索して使用します。

> [!WARNING]
> Bypass Listener Effects が有効化された AudioSource の音声はキャプチャされません。

シーン上に複数の AudioListener が存在する場合は、`InstantReplay.UnityAudioSampleProvider` のコンストラクタに AudioListener を渡して初期化し、`InstantReplaySession` のコンストラクタに `audioSampleProvider` として渡してください。

```csharp
new InstantReplaySession(900, audioSampleProvider: new UnityAudioSampleProvider(audioListener), disposeAudioSampleProvider: true);
```

音声ソースを無効化したい場合は、`NullAudioSampleProvider.Instance` を使用してください。

```csharp
new InstantReplaySession(900, audioSampleProvider: NullAudioSampleProvider.Instance);
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

new InstantReplaySession(900, audioSampleProvider: new CustomAudioSampleProvider(), disposeFrameProvider: true);

```

### 録画状態を取得する

`InstantReplaySession.State` プロパティで録画の状態を取得できます。

### 書き出しの進捗状況を取得する

`InstantReplaySession.StopAndTranscodeAsync()` の引数に `IProgress<float>` を渡すことで書き出しの進捗状況を 0.0〜1.0 で取得できます。
