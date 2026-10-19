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
  * [Instant Replay Suite](#instant-replay-suite)
    * [セットアップ](#セットアップ)
    * [UI の操作](#ui-の操作)
      * [オーバーレイ](#オーバーレイ)
      * [Recorder タブ](#recorder-タブ)
      * [トリムビュー](#トリムビュー)
      * [Recordings タブ](#recordings-タブ)
      * [未完了の録画の復旧](#未完了の録画の復旧)
    * [コードから Suite を使用する](#コードから-suite-を使用する)
      * [リプレイの保存](#リプレイの保存)
      * [無制限録画の開始](#無制限録画の開始)
      * [状態と統計情報](#状態と統計情報)
      * [映像・音声ソースの指定](#映像音声ソースの指定)
      * [録画ライブラリ](#録画ライブラリ)
      * [共有](#共有)
      * [UI を使用せずにレコーダーを使用する](#ui-を使用せずにレコーダーを使用する)
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

インポートしたサンプルの `SampleScene` を開き、Play モードに入ります。このシーンには `InstantReplaySuite` コンポーネントを持つゲームオブジェクトが含まれており、シーンの開始と同時にゲームプレイのバッファリングを開始します。

画面右上のオーバーレイには、バッファ済みの映像の長さと、バッファに収まる長さの推定値が表示されます。オーバーレイをクリックまたはタップするとパネルが開きます。

<img width="800" alt="Instant Replay Suite の Recorder タブ" src=".github/images/suite-recorder.png" />

**Save replay** を押すと、バッファ済みの映像全体を選択した状態でトリムビューが開きます。**Save** を押すとそのまま保存されます。範囲を選択してから保存する方法は [トリムビュー](#トリムビュー) を参照してください。

<img width="800" alt="Instant Replay Suite のトリムビュー" src=".github/images/suite-trim.png" />

保存した録画は **Recordings** タブに表示され、再生、共有、削除を行えます。

<img width="800" alt="Instant Replay Suite の Recordings タブ" src=".github/images/suite-recordings.png" />

自身のプロジェクトで Suite を使用する方法は [Instant Replay Suite](#instant-replay-suite) を、Suite を使用せずにコードから録画する方法は [詳細な使い方](#詳細な使い方) を参照してください。

## Instant Replay Suite

Instant Replay Suite は、パッケージに含まれるランタイムコンポーネントと UI のセットです。録画 API の上に多くのプロジェクトが実装する機能、すなわち常時バッファリング、リプレイの保存とトリミング、無制限録画、実行時にユーザーが変更できる録画設定、保存した録画のライブラリ、共有、および異常終了時に残った映像の復旧を提供します。

Suite は以下のアセンブリで構成されます。

| アセンブリ | 内容 |
|---|---|
| `InstantReplay.Suite` | レコーダー (`InstantReplayRecorder`)、プリセット、設定アセット、録画ライブラリ、共有の抽象化、ファイル書き出しの共有先。UI フレームワークに依存しません。 |
| `InstantReplay.Suite.UI` | `InstantReplaySuite` コンポーネントと UI Toolkit による UI。 |
| `InstantReplay.Suite.Sharing.Slack` | Slack の共有先。UnityWebRequest モジュールが有効な場合のみコンパイルされます。 |
| `InstantReplay.Suite.Sharing.Discord` | Discord の共有先。UnityWebRequest モジュールが有効な場合のみコンパイルされます。 |

録画のプレビューには Video モジュール、サムネイルには Image Conversion モジュールが必要です。これらのモジュールが無効な場合、対応するコントロールは表示されません。WebGL では `VideoPlayer` がローカルファイルを開けないため、プレビューは利用できません。

> [!NOTE]
> Suite はデフォルトで `ScreenshotFrameProvider` により画面全体を録画するため、オーバーレイとパネルも録画に映ります。これらを除外するには、[映像・音声ソースの指定](#映像音声ソースの指定) の方法でカメラを対象とするフレームプロバイダーを指定してください。

### セットアップ

1. 最初のシーンのゲームオブジェクトに `InstantReplaySuite` コンポーネントを追加します (**Add Component > Instant Replay > Instant Replay Suite**)。同時に有効にできるインスタンスは 1 つだけで、2 つ目のインスタンスは自身を破棄します。
2. **Create > Instant Replay > Suite Settings** から設定アセットを作成し、**Settings** フィールドに割り当てます。設定アセットがない場合は、デフォルトのプリセット 1 つが使用されます。

<img width="520" alt="InstantReplaySuite コンポーネントのインスペクター" src=".github/images/suite-component.png" />

| フィールド | 説明 |
|---|---|
| Settings | `InstantReplaySuiteSettings` アセット。 |
| Theme Style Sheet, Style Sheet | UI のスタイルシート。エディター上でパッケージから自動的に割り当てられます。 |
| Sorting Order | UI を表示するパネルの描画順。 |
| Dont Destroy On Load | シーンの読み込みをまたいでゲームオブジェクトを保持するかどうか。 |
| Show Overlay On Launch | 起動時にオーバーレイを表示するかどうか。オーバーレイを非表示にしている間も録画は継続します。 |
| Scale | UI の大きさに掛ける倍率。 |

UI はどの画面でも一定の物理サイズで表示されます。スタイルシートの 1 単位は、デスクトッププラットフォームでは 1/96 インチ、携帯端末では 1/160 インチに相当します。

<img width="520" alt="InstantReplaySuiteSettings アセットのインスペクター" src=".github/images/suite-settings.png" />

| フィールド | 説明 |
|---|---|
| Presets | ユーザーが選択できる録画設定。後述します。 |
| Default Preset Id | ユーザーがプリセットを選択するまで使用するプリセットの ID。 |
| Start Buffering On Launch | ユーザーが変更していない場合に、起動時にバッファリングを開始するかどうか。 |
| Output Directory | 録画の保存先ディレクトリ。相対パスは `Application.persistentDataPath` を基準に解決されます。 |
| Max Recordings | 保持する録画の数。これを超えると古い録画から削除されます。0 の場合はすべて保持します。 |
| Max Total Megabytes | 保持する録画の合計サイズ (MiB)。これを超えると古い録画から削除されますが、保存した直後の録画は常に保持されます。0 の場合はすべて保持します。 |
| Persist User Preferences | ユーザーが選択したプリセットとバッファリングの有効・無効を次回起動時に復元するかどうか。 |
| Share Targets | 録画の共有時に表示する共有先。[共有](#共有) を参照してください。 |

各プリセットは、ID、表示名、解像度 (固定サイズ、または長辺の上限付きの画面サイズに対する比率)、フレームレート、映像のビットレート (固定値、または 1 ピクセルあたりのビット数からの算出)、音声のビットレート、バッファ (メモリまたはディスク、およびそのサイズ (MiB))、`ForceReadback` を持ちます。パネルには、選択中のプリセットのバッファに収まる長さの推定値が表示されます。

### UI の操作

#### オーバーレイ

オーバーレイは有効な間は常に表示されます。色付きのドットでレコーダーの状態を示し、バッファ済みの長さと推定容量を表示します。無制限録画中はドットが赤く点滅し、経過時間を表示します。オーバーレイをクリックまたはタップするとパネルが開閉します。

#### Recorder タブ

- **Continuous buffering** でバッファリングを有効または無効にします。
- **Save replay** はバッファ済みの映像を取り出して [トリムビュー](#トリムビュー) を開きます。バッファリングは直ちに再開されるため、トリムビューを開いている間も映像はバッファされ続けます。
- **Start recording** は無制限録画を開始し、**Stop recording** を押すまでファイルに直接書き込みます。無制限録画中はバッファリングが中断され、終了後に再開されます。
- **Pause** はバッファリングまたは無制限録画を一時停止し、**Resume** で再開します。
- **Preset** でプリセットを選択します。プリセットを変更すると、バッファ済みの映像は破棄されます。
- **Customize** では、選択中のプリセットの個々の設定を変更できます。変更したプリセットには "(custom)" が付き、**Reset to preset** で変更を破棄できます。

<img width="800" alt="Recorder タブの Customize" src=".github/images/suite-customize.png" />

#### トリムビュー

トリムビューは、取り出した映像を選択範囲内でループ再生します。

- タイムラインの両端のハンドルをドラッグして範囲を選択します。開始位置は、保存される録画が実際に始まるキーフレームに吸着します。タイムラインの下の目盛りはキーフレームの位置を示します。
- ノブをドラッグするか、タイムラインの他の場所をクリックすると、再生位置が移動します。**Set start** と **Set end** は、再生位置を範囲の開始または終了に設定します。
- **Use frame as thumbnail** は、表示中のフレームを録画のサムネイルにします。
- **Save** は選択範囲をライブラリに保存し、**Discard** は保存せずにビューを閉じます。

#### Recordings タブ

Recordings タブには、保存した録画が新しい順に表示されます。録画を選択すると、プレビューと詳細が表示されます。

<img width="800" alt="録画の詳細" src=".github/images/suite-detail.png" />

- **Use frame as thumbnail** は、プレビューに表示中のフレームでサムネイルを置き換えます。
- **Delete** は録画を削除します。確認のため、3 秒以内にもう一度押す必要があります。
- **Share to** で共有先を選択し、**Comment** にメッセージを入力して、**Share** で共有します。この項目は共有先が 1 つ以上設定されている場合に表示されます。
- **Select** を押すと選択モードになり、複数の録画を選択してまとめて削除できます。

#### 未完了の録画の復旧

ディスクバッファを使用するプリセットで録画中にアプリケーションが異常終了すると、ディスクにバッファされた映像が残ります。次回起動時、Recordings タブの **Unfinished recordings** にその映像が表示されます。**Recover** でライブラリに保存し、**Delete** で破棄します。

### コードから Suite を使用する

ゲームのコードからは、`InstantReplaySuite.Recorder` で取得できる `InstantReplayRecorder` を通じて Suite を操作できます。`InstantReplaySuite.Current` は有効なインスタンスを返します。`InstantReplayRecorder` のメンバーはすべてメインスレッドから呼び出す必要があり、イベントもメインスレッドで発生します。

#### リプレイの保存

`SaveReplayAsync` は、ユーザーの操作を介さずにバッファ済みの映像をライブラリに保存します。

```csharp
using System.Collections.Generic;
using InstantReplay.Suite;
using InstantReplay.Suite.UI;
using UnityEngine;

var recorder = InstantReplaySuite.Current.Recorder;
if (recorder.State == RecorderState.Buffering)
{
    var entry = await recorder.SaveReplayAsync(new ReplaySaveRequest
    {
        DurationSeconds = 30, // null の場合はバッファ全体を保存
        Metadata = new Dictionary<string, string> { ["scene"] = "Stage1" }
    });
    Debug.Log($"Saved {entry.FilePath}");
}
```

`CaptureReplayAsync` は、バッファ済みの映像を保存せずに取り出し、`ReplayDraft` を返します。`ReplayDraft` からは任意の範囲を何度でも保存できます。`ReplayDraft` は呼び出し側が所有し、破棄する必要があります。

```csharp
using var draft = await recorder.CaptureReplayAsync();

// 最後の 10 秒を保存する。開始位置は指定した時刻以前のキーフレームに移動する。
var start = draft.GetSaveStart(Math.Max(0, draft.DurationSeconds - 10));
var entry = await draft.SaveAsync(start);
```

#### 無制限録画の開始

```csharp
var recording = recorder.StartUnbounded();

// 〜 ゲームプレイ 〜

var entry = await recording.StopAsync();
```

#### 状態と統計情報

`State` は `Idle`、`Buffering`、`Exporting`、`RecordingUnbounded`、`Faulted` のいずれかを返し、変化すると `StateChanged` が発生します。`IsPaused` は状態とは独立しています。`GetStatistics()` は、バッファ済みの長さ、推定容量、バッファの使用バイト数、および無制限録画の経過時間を返します。

Recorder タブのコントロールに対応する `SetBufferingEnabled`、`Pause`、`Resume`、`ApplyPreset` でもレコーダーを操作できます。

#### 映像・音声ソースの指定

Suite はデフォルトで `ScreenshotFrameProvider` と `UnityAudioSampleProvider` を使用します。他のプロバイダーを使用するには、`InstantReplaySuite` コンポーネントの `Start` より前、たとえば自身のコンポーネントの `Awake` で指定します。

```csharp
using InstantReplay;
using InstantReplay.Suite.UI;
using InstantReplay.UniversalRP;
using UnityEngine;

public class SuiteSetup : MonoBehaviour
{
    [SerializeField] private InstantReplaySuite suite;
    [SerializeField] private Camera gameCamera;

    private void Awake()
    {
        // ゲームのカメラのみを録画し、オーバーレイとパネルが録画に映らないようにする。
        suite.Recorder.SetFrameProviderFactory(() => new RendererFeatureFrameProvider(gameCamera));
    }
}
```

レコーダーは各プロバイダーのインスタンスを最大 1 つだけ保持し、作成するすべてのセッションに同じインスタンスを渡します。そのため、ファクトリーはインスタンスが存在しない場合にのみ呼び出されます。`SetFrameProvider` と `SetAudioSampleProvider` は、アプリケーションが作成したインスタンスを受け取ります。`takeOwnership` が false の場合、レコーダーはそのインスタンスを破棄しません。カメラが破棄されるなどしてプロバイダーが動作できなくなった場合は、`InvalidateFrameProvider` または `InvalidateAudioSampleProvider` を呼び出してください。

#### 録画ライブラリ

`recorder.Library` は保存した録画を管理します。`GetEntries()` は録画を新しい順に `RecordingEntry` として返します。`RecordingEntry` は、ファイルパス、作成日時、長さ、サイズ、録画の種類 (`Replay`、`Unbounded`、`Recovered`)、メタデータ、サムネイルのパスを持ちます。`Delete` で録画を削除でき、一覧が変化すると `Changed` が発生します。`ImportRecoveredAsync` は、`DiskEncodedFrameBufferRecovery.FindRecoverable()` が返す映像をライブラリに保存します。

#### 共有

共有先は `ShareTarget` を継承したアセットで、設定アセットの **Share Targets** に登録します。パッケージは以下の共有先を提供しており、いずれも **Create > Instant Replay > Share Targets** から作成できます。

| 共有先 | 説明 |
|---|---|
| File Export | 録画をディレクトリにコピーします。相対パスは `Application.persistentDataPath` を基準に解決されます。デスクトッププラットフォームでは、コピー後にディレクトリを開くことができます。 |
| Slack | `files:write` スコープを持つ Bot トークンを使用して、録画をチャンネルにアップロードします。 |
| Discord | Webhook を通じて録画をチャンネルに投稿します。サーバーのアップロード上限を設定できます (デフォルトは 10 MiB)。 |

> [!WARNING]
> アセットに設定したトークンと Webhook URL はビルドに含まれます。Slack と Discord の共有先は、開発用および QA 用のビルドでの使用を想定しています。認証情報は、実行時にコードから `SlackShareTarget.Token` と `DiscordShareTarget.WebhookUrl` で設定することもできます。

共有先は `recorder.ShareTargets` から実行時に追加することもできます。他の共有先に対応するには、`ShareTarget` を継承したクラスを作成します。

```csharp
using System.Threading;
using System.Threading.Tasks;
using InstantReplay.Suite;
using UnityEngine;

[CreateAssetMenu(menuName = "My Game/Bug Tracker Share Target")]
public class BugTrackerShareTarget : ShareTarget
{
    public override bool CanShare(ShareRequest request, out string reason)
    {
        reason = null;
        return true;
    }

    public override async ValueTask<ShareResult> ShareAsync(ShareRequest request, ShareContext context,
        CancellationToken cancellationToken)
    {
        foreach (var entry in request.Entries)
        {
            // entry.FilePath を request.Comment と entry.Metadata とともにアップロードし、
            // context.ReportProgress(0..1) で進捗を報告する。
        }

        // 失敗は例外を投げて報告する。例外のメッセージはユーザーに表示される。
        return new ShareResult("Reported.", url: "https://example.com/issues/123");
    }
}
```

#### UI を使用せずにレコーダーを使用する

独自の UI を作成するプロジェクトは、`InstantReplay.Suite` アセンブリの `InstantReplayRecorder` を直接使用できます。

```csharp
using InstantReplay.Suite;

var recorder = new InstantReplayRecorder(settings); // null の場合は組み込みのデフォルトを使用
recorder.Activate(); // ユーザーの選択を復元し、有効な場合はバッファリングを開始

// 〜 ゲームプレイ 〜

recorder.Dispose();
```

## 詳細な使い方

Suite を使用せずにコードから録画を行うには、`RealtimeInstantReplaySession` を使用します。

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
