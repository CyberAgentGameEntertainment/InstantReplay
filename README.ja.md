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
      * [ビルトインの `IFrameProvider`](#ビルトインの-iframeprovider)
      * [カスタム `IFrameProvider` の実装](#カスタム-iframeprovider-の実装)
    * [音声ソースの設定](#音声ソースの設定)
      * [CRI サポート](#cri-サポート)
      * [Wwise サポート](#wwise-サポート)
    * [録画状態を取得する](#録画状態を取得する)
  * [ディスクバッファリングとクラッシュ復旧](#ディスクバッファリングとクラッシュ復旧)
    * [クラッシュ後の復旧](#クラッシュ後の復旧)
    * [ディスクバッファの設定](#ディスクバッファの設定)
  * [無制限録画](#無制限録画)
  * [レガシーモード](#レガシーモード)
    * [録画時間とフレームレートの設定](#録画時間とフレームレートの設定)
    * [サイズの設定](#サイズの設定)
    * [映像・音声ソースの設定](#映像音声ソースの設定)
  * [リリースビルドから除外する](#リリースビルドから除外する)
  * [ライセンス](#ライセンス)
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
Web|(any)|(any)|(any)|[WebCodecs をサポートするブラウザ](#使用されるエンコーダー-api)

- レガシーモードでは、他のプラットフォームでも `ffmpeg` が PATH に存在すれば動作する可能性があります。

> [!WARNING]
> **WebGL での既知の問題**: WebGL では、録画中に画面にフリッカーが発生する可能性があります。これはデフォルトの `IFrameProvider` 実装である `ScreenshotFrameProvider` に起因するもので、この問題が発生した場合は、[`BuiltinCameraFrameProvider`](#ビルトインの-iframeprovider) (Built-in RP 用)、[`RendererFeatureFrameProvider`](#ビルトインの-iframeprovider) (Universal RP 用)、またはその他のカスタム `IFrameProvider` 実装を使用して、入力 `RenderTexture` を直接提供することで回避できます。

### 使用されるエンコーダー API

Platform|APIs
-|-
iOS / macOS|Video Toolbox (H.264), Audio Toolbox (AAC)
Android|MediaCodec (H.264 / AAC)
Windows|Media Foundation (H.264 / AAC)
Linux|システムにインストールされたFFmpeg (H.264 / AAC)
Web|[WebCodecs](https://caniuse.com/webcodecs) (`avc1.640028` for video, `mp4a.40.2` for audio)

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

`IFrameProvider` を使用して映像ソースのカスタマイズが可能です。

`RealtimeInstantReplaySession` のコンストラクタに `frameProvider` として渡してください。また `disposeFrameProvider` によって `RealtimeInstantReplaySession` 側で `frameProvider` を自動的に破棄するかどうかを指定できます。

```csharp

new RealtimeInstantReplaySession(options, frameProvider: new ScreenshotFrameProvider(), disposeFrameProvider: true);

```

#### ビルトインの `IFrameProvider`

- `ScreenshotFrameProvider`: デフォルトで使用される実装です。`ScreenCapture.CaptureScreenshotIntoRenderTexture()` を使用するため、Overlay Canvas などカメラに含まれない描画結果もキャプチャできます。キャプチャ用に追加の RenderTexture を使用するため、GPU メモリ使用量が増加します。
- `BuiltinCameraFrameProvider`: Built-in Render Pipeline で `OnRenderImage()` を使用して特定のカメラの映像をキャプチャします。
- `RendererFeatureFrameProvider`: Universal Render Pipeline で Renderer Feature を使用して特定のカメラの映像をキャプチャします。カメラに対応する Renderer に対して `InstantReplayFrameRendererFeature` を追加する必要があります。

#### カスタム `IFrameProvider` の実装

`InstantReplay.IFrameProvider` を継承したクラスを作成します。

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

#### CRI サポート

InstantReplay は [CRIWARE](https://game.criware.jp/) からの音声をキャプチャするための `IAudioSampleProvider` 実装を提供しています。

1. CRIWARE Unity Plug-in をインストールします。
2. Player Settings でシンボル `INSTANTREPLAY_CRI` を追加します。
3. 必要な場合は `InstantReplay.Cri` アセンブリ参照を追加します。
4. `RealtimeInstantReplaySession` コンストラクタの `audioSampleProvider` に `InstantReplay.Cri.CriAudioSampleProvider` を指定します。

#### Wwise サポート

Wwise もサポートされています。

1. Wwise Unity Integration をインストールします。
2. Player Settings でシンボル `INSTANTREPLAY_WWISE` を追加します。
3. 必要な場合は `InstantReplay.Wwise` アセンブリ参照を追加します。
4. `RealtimeInstantReplaySession` コンストラクタの `audioSampleProvider` に `InstantReplay.Wwise.WwiseAudioSampleProvider` を指定します。

### 録画状態を取得する

`InstantReplaySession.State` プロパティで録画の状態を取得できます。

## ディスクバッファリングとクラッシュ復旧

`RealtimeInstantReplaySession` は既定ではエンコード済みのフレームをメモリ上に保持しますが、`RealtimeEncodingOptions.DiskBuffer` を設定するとディスク上のセグメントファイルに書き出すようになります。メモリ使用量を抑えられるほか、異常終了の直前までの映像がディスク上に残るため、次回起動時に復旧できます。

> [!WARNING]
> ストレージへ継続的に書き込むとフラッシュメモリの寿命を縮めます。この機能は主に開発ビルドや QA ビルドでの利用を想定しており、エンドユーザーに配布するビルドでの使用は推奨しません。

```csharp
using InstantReplay;

var options = RealtimeEncodingOptions.Default;
options.DiskBuffer = DiskBufferOptions.Default;

using var session = new RealtimeInstantReplaySession(options);
```

ディスクバッファが有効な間は `MaxMemoryUsageBytesForCompressedFrames` は使用されません。代わりに `DiskBufferOptions.MaxDiskUsageBytes` が保持されるデータ量の上限になります。

### クラッシュ後の復旧

各セッションは専用のディレクトリに書き込みます。正常に破棄されたセッションは、`RetainOnDispose` が設定されていない限り自身のディレクトリを削除します。したがって次回起動時に残っているディレクトリは、正常に終了しなかったセッションを表します。

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

`FindRecoverable` は指定したルートディレクトリ以下の復旧可能なセッションを列挙します。引数を省略した場合は、記録側が既定で使用するディレクトリを対象とします。異常終了が複数回発生した場合は複数のセッションが残ることがあり、どれを書き出してどれを破棄するかは呼び出し側が決定します。パスが既に判明している単一のセッションを読み取る場合は `TryGetRecoverable` を使用します。

復旧処理が暗黙にディレクトリを削除することはありません。クラッシュによって残されたセッションは、それに先行する映像の唯一の複製だからです。書き出したファイルの処理が済んだ時点で `Delete()` を呼び出してください。

`IsCompatible` は、マニフェストに記録されたプラットフォームとパッケージバージョンを実行中のアプリケーションと比較します。ただしこれは必要条件であって十分条件ではありません。ペイロードはネイティブライブラリによってシリアライズされており、そのスキーマはパッケージのバージョン間で変わりうるためです。この種の不一致は `ExportAsync` の失敗として表面化します。

### ディスクバッファの設定

| プロパティ | 既定値 | 説明 |
|---|---|---|
| `Directory` | `null` | セッションディレクトリを格納するディレクトリ。`null` または空の場合は `Application.temporaryCachePath/InstantReplay/DiskBuffer` が使用されます (`DiskBufferOptions.GetDefaultDirectory()` で取得できます)。 |
| `MaxDiskUsageBytes` | 256 MiB | 1 セッションディレクトリのサイズ上限。マニフェスト、コーデック設定、全セグメントを含みます。`DiskBufferOptions.MinimumDiskUsageBytes` (4 MiB) 以上である必要があります。 |
| `SegmentDuration` | 5.0 | 1 セグメントファイルの目標長さ (秒)。 |
| `MaxSegmentBytes` | 8 MiB | 1 セグメントファイルのサイズ上限。`MaxDiskUsageBytes` を超えることはできません。 |
| `MaxPendingWriteBytes` | 4 MiB | 書き込みキューで待機するペイロードの合計サイズ上限。キューが一杯の間に到着したフレームは、エンコーダーをブロックせず破棄されます。 |
| `RetainOnDispose` | `false` | セッションが正常に破棄されたときにディレクトリを残すかどうか。 |
| `SyncMode` | `OperatingSystem` | フラッシュ方針。下記を参照してください。 |

`MaxDiskUsageBytes` は目標値ではなく上限です。各レコードの書き込み前に領域を予約し、その予約が必要なだけ古いセグメントを削除するため、ディレクトリのサイズが一瞬たりとも上限を超えることはありません。削除可能なセグメントをすべて削除しても上限を満たせない場合は、書き込まずにレコードを破棄します。したがって `MaxSegmentBytes` に近い値を指定すると、保持時間ではなく録画そのものが劣化します。セグメントは映像のキーフレームで閉じられるため、セグメントを1つ破棄しても不完全な GOP が残ることはありません。

`SyncMode` は、どの障害モードに備えるかを選択します。既定の `DiskBufferSyncMode.OperatingSystem` は、書き込みキューから取り出したバッチごとにデータを OS へ渡し、セグメントを閉じる際にストレージデバイスへフラッシュします。記録されたフレームはプロセスのクラッシュ (ネイティブフォールト、OOM kill、abort) を生き延びます。これがクラッシュ復旧の目的とする障害モードであり、デバイスへの追加の書き込みを必要としません。電源断やカーネルパニックでは、現在のセグメントを開いてから書き込んだレコードが失われる可能性があります。`DiskBufferSyncMode.EveryRecord` は全レコードをデバイスへフラッシュするため電源断にも耐えますが、フレームごとに1回のデバイスフラッシュが発生します。フラッシュメモリの摩耗が著しく増えるため、常用ではなくストレージ層の問題の診断を想定しています。

## 無制限録画

`UnboundedRecordingSession` を使用すると、エンコードしたデータをメモリに保持せず直接ディスク上の MP4 ファイルに書き出します。書き出せる動画ファイルの時間には制限が設定されず、ディスク容量の許す限り録画が行えます。コンストラクタで出力ファイルパスの指定が必要な以外は `RealtimeInstantReplaySession` と同様に使用できます。

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

## リリースビルドから除外する

バグ収集の一部として **InstantReplay** を使用している場合は、リリース ビルドでスクリプト ファイルとプラグイン ファイルを除外する必要があります。

**Scripting Define Symbols** に **EXCLUDE_INSTANTREPLAY** を 加えると **InstantReplay** に関連する全てのコードがコンパイル対象から除外されます。  
したがって、**InstantReplay** にアクセスするコードをすべて`#if !EXCLUDE_INSTANTREPLAY` と `#endif`で囲っておけば、リリース時に関連するスクリプトを全て除外できます。

## ライセンス

[MIT](LICENSE)

使用されている依存関係のライセンスについては [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。
