# パフォーマンスガイド

本文書は、パフォーマンス要件の厳しいプロジェクトで Instant Replay を利用する開発者を対象に、`RealtimeInstantReplaySession` が使用するメモリと CPU / GPU 処理の内訳、および各設定がそれらに与える影響を説明する。後半には実機での計測結果を掲載する。

計測値は特定の端末、シーン、設定における一例であり、他の環境での値を保証するものではない。自身のプロジェクトでの影響は、本文書で説明する方法で実機上で確認すること。本文書の説明は、[v1.7.0 以降の変更](#v170-以降の変更) に記載した変更を含む、Instant Replay v1.7.0 より後のバージョンを対象とする。計測値は、特に記載がない限り v1.7.0 で計測したものである。

英語版は [performance-guide.md](performance-guide.md) にある。

## 処理の概要

録画中は、毎フレーム次の処理が行われる。

1. フレームのキャプチャ: 既定の `ScreenshotFrameProvider` は、フレームの最後に `ScreenCapture.CaptureScreenshotIntoRenderTexture()` で画面全体を画面サイズの RenderTexture にコピーする。
2. エンコーダーへの入力: キャプチャしたテクスチャを、エンコードサイズ (`VideoOptions.Width` × `VideoOptions.Height`) に縮小してエンコーダーに渡す。経路は次の 2 種類がある。
   - blit 経路: ネイティブプラグインが render thread 上で GPU を使い、エンコーダーの入力サーフェスに直接描画する。iOS / macOS (Metal) と Android (Vulkan、API 29 以上) で使用される。
   - readback 経路: Unity 側で縮小したテクスチャを `AsyncGPUReadback` で CPU メモリに読み戻し、エンコーダーに渡す。blit 経路に対応しないプラットフォーム (Android の OpenGL ES、Windows、Linux、Web など) と、`ForceReadback` が有効な場合に使用される。
3. エンコード: プラットフォームのエンコーダー (VideoToolbox、MediaCodec、Media Foundation など) が H.264 と AAC にエンコードする。音声は `OnAudioFilterRead` で取得したサンプルをエンコードする。
4. バッファリング: エンコード済みのデータを C# 側にコピーし、メモリ上のリングバッファに保持する。ディスクバッファが有効な場合は、ファイルに書き出す。

`StopAndExportAsync()` を呼び出すと、バッファ内のデータを MP4 ファイルに多重化 (mux) する。エンコードはやり直さない。

## メモリ

### 内訳

| 項目 | サイズの目安 | 条件 | Memory Profiler での表示 |
| --- | --- | --- | --- |
| キャプチャ用 RenderTexture | 画面の幅 × 高さ × 4 バイト | `ScreenshotFrameProvider` を使用する場合 | RenderTexture `InstantReplay ScreenshotFrameProvider` |
| 縮小用 RenderTexture | エンコードの幅 × 高さ × 4 バイト | readback 経路 | RenderTexture `InstantReplay Preprocessing` |
| エンコード前のフレーム (CPU) | エンコードの幅 × 高さ × 4 バイト × 枚数 | readback 経路 | UniEnc のネイティブヒープ (後述) |
| エンコーダーの入力サーフェス | エンコードの幅 × 高さ × 4 バイト × 数枚 | blit 経路 | 表示されない |
| エンコーダー内部のメモリ | 不定 | 常に | 表示されない |
| エンコード済みデータのバッファ | `MaxMemoryUsageBytesForCompressedFrames` 以下 | メモリバッファ | マネージドヒープの 64 KiB の `System.Byte[]` |
| ディスクバッファの書き込みキュー | `DiskBufferOptions.MaxPendingWriteBytes` 以下 | ディスクバッファ | マネージドヒープの `System.Byte[]` |
| UniEnc のネイティブヒープ | 32 MiB 単位で確保される | 常に | `UniEnc` / `mimalloc` |

以下、各項目を補足する。

#### キャプチャ用 RenderTexture

`ScreenshotFrameProvider` は、画面サイズの ARGB32 RenderTexture を 1 枚確保する。エンコードサイズではなく画面サイズに比例するため、高解像度の端末ほど大きくなる。例えば 1080 × 2340 の画面では約 9.8 MB となる。独自の `IFrameProvider` を使用する場合、このテクスチャは確保されない。

#### エンコード前のフレーム

readback 経路では、`AsyncGPUReadback` の読み戻し先として、エンコードサイズ × 4 バイトのバッファをフレームごとに確保する。同時に存在するバッファの数は `MaxNumberOfRawFrameBuffers` で制限できる。既定値 (null) では上限が設けられず、実際の枚数はエンコーダーの処理が追いつくまでの間に読み戻されたフレームの数で決まる。上限に達したフレームは破棄されるため、値を小さくしすぎるとフレーム落ちが起こる。

blit 経路では、GPU からエンコーダーの入力サーフェス (iOS では IOSurface を持つ `CVPixelBuffer`、Android では `AHardwareBuffer`) に直接描画するため、CPU 側のバッファは使用しない。入力サーフェスはエンコードサイズ × 4 バイトで、iOS では 2～4 枚、Android では最大 3 枚が確保される。これらは OS が管理するメモリであり、Unity のメモリ管理の対象外である。

#### エンコード済みデータのバッファ

メモリバッファでは、エンコード済みのフレームをマネージドヒープ上の 64 KiB のチャンクにコピーする。映像と音声のストリームは 1 つのプールからチャンクを取得し、チャンクの合計サイズは `MaxMemoryUsageBytesForCompressedFrames` を超えない。チャンクは初めて必要になった時点で確保され、含まれるフレームがすべて破棄された後に再利用されるため、マネージドヒープはバッファが満たされるまで増加し、バッファが上限に達した時点で増加が止まる。各チャンクは大きなオブジェクトのヒープ (LOH) の閾値より小さい。バッファが満杯のときは、映像と音声を通じてタイムスタンプが古い順にフレームが破棄される。チャンクはセッションを破棄した時点で解放される。

Android での計測では、満杯時のマネージドヒープの使用量 (GC Used Memory) が、既定の 20 MiB に対して約 23 MB、60 MiB に対して約 70 MB 増加した。これらの値には、バッファ以外の数 MB の確保が含まれる。

v1.7.0 では、エンコード済みのフレームを `ArrayPool<byte>.Shared` から借りた `byte[]` に格納していた。`ArrayPool<byte>.Shared` は要求サイズを 2 のべき乗に切り上げた配列を返すため、マネージドヒープの使用量は 20 MiB に対して 35～50 MB、60 MiB に対して約 103 MB 増加した。また、エクスポートを行った場合、セッションを破棄した後もバッファがマネージドヒープに残ることがあった。

マネージドヒープは一度拡張されると縮小されにくいため、録画を終了した後も GC Reserved Memory は増加したままとなる。

ディスクバッファでは、エンコード済みのフレームは書き込みキューを経由してファイルに書き出され、メモリには保持されない。キューのサイズは `DiskBufferOptions.MaxPendingWriteBytes` (既定値 4 MiB) を上限とする。

#### UniEnc のネイティブヒープ

ネイティブライブラリ (unienc) 内部のメモリ確保は、Unity のメモリマネージャーを経由して行われ、Memory Profiler では `UniEnc` という領域の `mimalloc` というオブジェクトとして表示される。この領域は 32 MiB 単位で確保されるため、Memory Profiler 上の値は実際に使用しているメモリよりも大きく、段階的に増加する。計測では、録画中のスナップショットで iOS、Android ともに約 68 MB と表示された。また、readback 経路のフレームバッファなどの大きな確保を契機として、さらに約 64 MiB 増加する場合があった。確保された領域は、セッションを破棄した後も解放されない。

Memory Profiler 上の値はアドレス空間の予約量であり、物理メモリの使用量とは一致しない。物理メモリへの影響は、OS が報告する値 (iOS の `phys_footprint`、Android の PSS) で確認すること。

#### Memory Profiler に表示されないメモリ

エンコーダーの入力サーフェス、ハードウェアエンコーダーの内部バッファ、Android の Java ヒープ上のオブジェクトは Unity のメモリ管理の対象外であり、Memory Profiler では Untracked に含まれるか、まったく表示されない。これらを確認するには、プラットフォームのツールを使用する。

- iOS: Xcode の Memory Report、Instruments の VM Tracker (IOSurface の領域)
- Android: `adb shell dumpsys meminfo <パッケージ名>` の Graphics と Private Other

### Memory Profiler での確認手順

1. 録画を開始し、バッファが満たされるまで待つ。メモリバッファでは、`GetBufferStatistics()` で得られる使用量が上限に近づいたことで判断できる。
2. Memory Profiler (`com.unity.memoryprofiler`) でスナップショットを取得する。
3. 次の項目を確認する。
   - Unity Objects: `InstantReplay ScreenshotFrameProvider` と `InstantReplay Preprocessing` の RenderTexture
   - All Of Memory の Native 以下に表示される `UniEnc` の領域
   - Managed Objects: `System.Byte[]` の合計サイズ。エンコード済みデータのチャンクは 65,536 バイトの配列であり、`InstantReplay.EncodedFrameTrackStorage` と `InstantReplay.EncodedFrameChunkPool` を経由して参照されている。
4. 同じ操作を録画していない状態でも行い、差分を比較する。

## CPU / GPU 処理

### Unity のスレッドで行われる処理

| スレッド | 処理 | Profiler での表示 |
| --- | --- | --- |
| Main Thread | 画面のキャプチャ | `Awaitable.OnEndOfFrame()` |
| Main Thread | パイプラインの非同期処理の継続 | `UnitySynchronizationContext.ExecuteTasks()` |
| Render Thread | blit 経路の縮小と色変換 | `UniEnc.Unity.GraphicsEventIssuer`、`unienc_android_mc::vulkan::preprocess::blit` (Android)、`unienc_apple_vt::metal::custom_blit` (iOS / macOS) |
| Render Thread | readback 経路の読み戻し | `Gfx.RequestAsyncReadbackData`、`Gfx.UpdateAsyncReadbackData` |
| Audio Mixer Thread | 音声サンプルの取得 | `UnityAudioSampleProviderReceiver.OnAudioFilterRead()` |

### Unity 外のスレッドで行われる処理

エンコーダーへの入力、エンコード済みデータの受け取り、C# 側へのコピーは、ネイティブライブラリのワーカースレッドとプラットフォームのエンコーダーのスレッドで行われる。これらのスレッドは Unity Profiler には表示されないか、名前のないスレッドとして表示される。CPU 使用量を確認するには、プラットフォームのツール (Xcode Instruments の Time Profiler、Android Studio の CPU Profiler や Perfetto) を使用する。Android では、ネイティブライブラリのスレッドは `Thread-<番号>` という名前で表示される。

これらの処理はフレームの描画とは並行して行われるため、フレーム時間には直接加算されない。ただし、端末全体の CPU 負荷と消費電力を増やし、発熱によるクロックの低下を通じてフレーム時間に影響することがある。消費電力と発熱については [encoder-power-consumption.md](encoder-power-consumption.md) を参照すること。

### 一時的な負荷

次の処理は、毎フレームではなく特定の時点で発生する。

- セッションの作成: `RealtimeInstantReplaySession` のコンストラクタはエンコーダーを初期化するため、計測では iOS で 30～110 ms、Android で 55～220 ms を要した。いずれもディスクバッファの場合に最も長かった。
- エクスポート: `StopAndExportAsync()` は、映像と音声の多重化をスレッドプール上で行う。Android でメモリバッファを使用した計測では、エクスポート中の最長のフレームは 33～50 ms であった。v1.7.0 では、多重化と、既定の出力ファイル名に使用するローカルタイムゾーンの初回の初期化が main thread 上で行われ、95～135 ms のフレームが 1 回発生していた。現在は、タイムゾーンはセッションの作成時にスレッドプール上で初期化される。スレッドを持たない Web では、これらの処理は引き続き main thread 上で行われる。

## 設定がメモリとフレーム時間に与える影響

### バッファの種類 (メモリ / ディスク)

メモリバッファは、エンコード済みのデータをマネージドヒープに保持する。保持できる長さは `MaxMemoryUsageBytesForCompressedFrames` とビットレートで決まり、マネージドヒープの使用量は最大でそのサイズ程度増加する。

ディスクバッファは、エンコード済みのデータをファイルに書き出すため、マネージドヒープはほとんど増加しない。v1.7.0 での計測では、満杯時の物理メモリの増加量がメモリバッファの約 40 % (iOS) と約 60 % (Android) となった。それ以降のバージョンでは、メモリバッファがフレームごとのサイズの切り上げを行わなくなったため、この差は小さくなる。一方で、ストレージへの継続的な書き込みによって Android では CPU 使用量が増加したほか、フラッシュメモリの寿命を縮める。ディスクバッファは主に開発ビルドと QA ビルドでの利用を想定している。詳細は [disk-buffered-recording.md](disk-buffered-recording.md) を参照すること。

### 画面サイズとエンコードサイズ

画面サイズは、キャプチャ用 RenderTexture のサイズと、キャプチャおよび縮小の GPU 負荷に影響する。エンコードサイズは、エンコーダーの入力サーフェス、readback 経路のフレームバッファ、エンコーダーの負荷に影響する。readback 経路ではフレームバッファの容量がエンコードサイズに比例するため、エンコードサイズの影響が特に大きい。計測では、Android の readback 経路でエンコードサイズを 1280 × 720 から 1920 × 1080 に変更すると、物理メモリの増加量が約 130 MB から約 210 MB に増加した。

### フレームレート

フレームレート (`FixedFrameRate` と `VideoOptions.FpsHint`) は、毎フレームの処理の回数に比例して CPU と GPU の負荷を増やす。計測では、30 fps から 60 fps に変更すると、プロセス全体の CPU 使用量の増加分が iOS で約 1.9 倍、Android で約 1.5 倍になった。同じバッファサイズで保持できる時間はビットレートで決まるため、フレームレートを上げても短くはならない。

### readback 経路

blit 経路が利用できるプラットフォームでも、`ForceReadback` を有効にすると readback 経路が使用される。readback 経路は、CPU メモリへの読み戻しとエンコード前のフレームバッファを必要とするため、blit 経路に比べて物理メモリの使用量と CPU 使用量が増加する。計測では、物理メモリの増加量が iOS で約 20 MB、Android で約 60 MB 多く、プロセスの CPU 使用量の増加分も iOS で約 2 倍となった。GPU 時間の増加量は、iOS では readback 経路の方が大きく、Android では blit 経路の方が大きかった。

エンコード待ちのフレームの数は `VideoInputQueueSize` (既定値 5) で制限されるが、読み戻し中のフレームはこれに含まれない。エンコーダーの処理が遅れた場合にフレームバッファが増え続けることを防ぐため、readback 経路を使用する場合は `MaxNumberOfRawFrameBuffers` を設定することを推奨する。なお、計測では上限を 3 に設定しても物理メモリの増加量はほとんど変わらなかったため、この設定はメモリ使用量の削減ではなく、増加の防止を目的とするものと考えること。

### ビットレート

ビットレートは、エンコーダーの処理量にはほとんど影響しないが、同じバッファサイズで保持できる時間を決める。保持できる時間は、おおむねバッファサイズ × 8 ÷ (映像のビットレート + 音声のビットレート) 秒である。例えば既定の 20 MiB、2.5 Mbps、128 kbps では約 64 秒となる。保持したい時間が決まっている場合、ビットレートを下げることでバッファサイズを小さくでき、メモリ使用量を削減できる。

ただし、Android の一部の端末では、エンコーダーが指定したビットレートに従わないことがある。計測に使用した端末では、1280 × 720 で 1 Mbps と 2.5 Mbps のどちらを指定しても実際のビットレートは約 4.4 Mbps となり、20 MiB で保持できる時間は約 38 秒であった。対象の端末で実際に保持できる時間を `GetBufferStatistics()` で確認すること。

### 音声

音声の録音を無効にする (`NullAudioSampleProvider` を使用する) と、音声のエンコードが行われなくなる。計測では、プロセスの CPU 使用量の増加分が iOS で約 40 %、Android で約 30 % 減少した。

## 推奨事項

- メモリバッファのサイズは、ビットレートと必要な録画時間から決定し、マネージドヒープの使用量がそのサイズ程度増加することを見込んでおく。
- readback 経路を使用する場合は、`MaxNumberOfRawFrameBuffers` を設定する。
- フレームレートとエンコードサイズは、用途に必要な最小限の値にする。特にフレームレートは CPU と GPU の負荷に直接影響する。
- セッションの作成は、フレーム落ちが許容される時点 (ロード中、リザルト画面など) に行う。エクスポートによって main thread が長時間停止することはなくなったが、エクスポート中のフレームには処理が加わる。
- Android では、対象の端末でビットレートと保持できる時間を確認する。
- 物理メモリへの影響は Memory Profiler だけでなく、OS が報告する値でも確認する。

## 計測結果

本節の値は v1.7.0 で計測したものである。それ以降の変更の影響を受ける値については、[v1.7.0 以降の変更](#v170-以降の変更) に記載する。

### 計測条件

| 項目 | iOS | Android |
| --- | --- | --- |
| 端末 | iPhone 12 | Galaxy S22 (SCG13, Snapdragon 8 Gen 1) |
| OS | iOS 26.7 | Android 16 (API 36) |
| グラフィックス API | Metal | Vulkan |
| 画面サイズ | 1170 × 2532 | 1080 × 2340 |

- Unity 6000.3.26f1、Universal Render Pipeline、IL2CPP、リリースビルドを使用した。
- 400 個の回転する立方体を描画するシーンを、60 fps を目標に実行した。
- 各条件でアプリケーションを新たに起動し、録画なしの状態を 20 秒計測した後にセッションを作成して、録画中の状態を 20 秒計測した。表の値は録画なしの状態からの増加量である。
- 既定の設定は、1280 × 720、30 fps、映像 2.5 Mbps、音声 128 kbps、メモリバッファ 20 MiB である。表の「条件」の列は既定の設定からの変更点を示す。
- 画面は縦向きであり、横長のエンコードサイズに縮小する際に左右に黒い帯が入る。そのため、映像の情報量は画面全体を使う場合よりも少ない。
- フレーム時間は `FrameTimingManager` で取得した。プロセスの CPU 使用量は、全スレッドの CPU 時間の合計を経過時間で割った値であり、1000 ms/s が 1 コアの全時間に相当する。
- 物理メモリは、iOS では `phys_footprint`、Android では PSS の合計である。「開始直後」は録画開始から 10 秒後、「満杯時」はバッファが上限に達した後の値である。

### iOS (iPhone 12)

| 条件 | Main Thread (ms) | GPU (ms) | プロセス CPU (ms/s) | 物理メモリ 開始直後 (MB) | 物理メモリ 満杯時 (MB) | GC Used Memory 満杯時 (MB) | 保持時間 (s) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 既定 | +0.19 | +0.76 | +70 | +28 | +66 | +44 | 66 |
| 640 × 360 | +0.37 | +0.69 | +80 | +19 | +59 | +43 | 67 |
| 1920 × 1080 | +0.30 | +0.82 | +75 | +40 | +76 | +41 | 66 |
| 60 fps | +0.52 | +0.80 | +132 | +29 | +72 | +49 | 65 |
| 1920 × 1080、60 fps | +0.63 | +0.85 | +139 | +41 | +83 | +47 | 65 |
| 1 Mbps | +0.26 | +0.76 | +73 | +28 | +79 | +48 | 162 |
| 5 Mbps | +0.19 | +0.75 | +57 | +33 | +62 | +40 | 33 |
| 10 Mbps | +0.28 | +0.76 | +78 | +37 | +69 | +42 | 16 |
| readback | +0.22 | +1.18 | +157 | +48 | +86 | +44 | 65 |
| readback、1920 × 1080 | -0.27 | +1.40 | +155 | +86 | +123 | +43 | 65 |
| ディスクバッファ | +0.06 | +0.81 | +70 | +26 | +27 | +2 | 75 |
| 音声なし | +0.24 | +0.77 | +41 | +27 | +64 | +43 | 66 |

録画なしの状態の値は、Main Thread 約 3.1 ms、GPU 約 2.0 ms、プロセス CPU 約 490 ms/s であった。

### Android (Galaxy S22)

| 条件 | Main Thread (ms) | GPU (ms) | プロセス CPU (ms/s) | 物理メモリ 開始直後 (MB) | 物理メモリ 満杯時 (MB) | GC Used Memory 満杯時 (MB) | 保持時間 (s) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 既定 | +0.42 | +2.61 | +465 | +45 | +73 | +37 | 38 |
| 640 × 360 | +0.5 | +2.85 | +478 | +40 | +76 | +42 | 63 |
| 1920 × 1080 | +0.62 | +2.67 | +506 | +46 | +82 | +37 | 63 |
| 60 fps | +0.75 | +4.53 | +680 | +43 | +84 | +39 | 38 |
| 1920 × 1080、60 fps | +0.68 | +4.42 | +686 | +43 | +97 | +51 | 63 |
| 1 Mbps | +0.54 | +2.64 | +484 | +43 | +71 | +37 | 38 |
| 5 Mbps | +0.59 | +2.61 | +488 | +46 | +76 | +37 | 32 |
| 10 Mbps | +0.52 | +2.65 | +490 | +52 | +77 | +38 | 16 |
| バッファ 60 MiB | +0.47 | +2.47 | +476 | +47 | +150 | +103 | 117 |
| readback | -0.42 | +0.40 | +570 | +97 | +132 | +33 | 38 |
| readback、`MaxNumberOfRawFrameBuffers` = 3 | -0.39 | +0.40 | +566 | +93 | +130 | +34 | 38 |
| readback、1920 × 1080 | -1.43 | +0.66 | +704 | +170 | +208 | +37 | 63 |
| ディスクバッファ | +0.59 | +2.59 | +551 | +41 | +46 | +2 | 76 |
| 音声なし | +0.27 | +2.53 | +335 | +45 | +80 | +35 | 39 |

録画なしの状態の値は、Main Thread 約 6.5 ms、GPU 約 2.7 ms、プロセス CPU 約 1010 ms/s であった。640 × 360 の Main Thread の値は中央値の差である (平均値は外れ値の影響を受けていたため)。

### Profiler で計測したフレームごとの処理時間

開発ビルドで、60 fps で描画し既定の設定 (30 fps) で録画している状態を Unity Profiler で計測し、主なマーカーの描画フレームあたりの平均時間 (self time) を求めた。録画するフレームは描画フレームの半分であるため、録画するフレームあたりの時間はこの値の約 2 倍である。iOS の readback 経路は計測していない。

| スレッド | マーカー | iOS blit 経路 (ms) | Android blit 経路 (ms) | Android readback 経路 (ms) |
| --- | --- | --- | --- | --- |
| Main Thread | `Awaitable.OnEndOfFrame()` | 0.06 | 0.19 | 0.25 |
| Main Thread | `UnitySynchronizationContext.ExecuteTasks()` | 0.03 | - | 0.11 |
| Render Thread | `unienc_apple_vt::metal::custom_blit` 以下の合計 | 0.17 | - | - |
| Render Thread | `unienc_android_mc::vulkan::preprocess::blit` 以下の合計 | - | 0.73 | - |
| Render Thread | `UniEnc.Unity.GraphicsEventIssuer` | 0.01 | 0.16 | - |
| Render Thread | `Gfx.RequestAsyncReadbackData` | - | - | 0.46 |
| Render Thread | `Gfx.UpdateAsyncReadbackData` | - | - | 1.53 |
| Audio Mixer Thread | `UnityAudioSampleProviderReceiver.OnAudioFilterRead()` | 0.06 | 0.28 | 0.28 |

### 考察

- iOS では、録画によるフレーム時間の増加は Main Thread、GPU ともに 1 ms 未満であった。Android では、blit 経路で GPU 時間が約 2.6 ms (60 fps では約 4.5 ms) 増加した。
- Android ではプロセスの CPU 使用量の増加が iOS より大きく、そのほとんどは Unity 外のスレッド (ネイティブライブラリのワーカースレッドと MediaCodec のスレッド) で発生していた。
- 物理メモリの増加量は、開始直後の時点ではキャプチャ用 RenderTexture、エンコーダー、ネイティブライブラリの初期化によるもので、その後バッファが満たされるにつれてマネージドヒープの増加分が加わる。
- ディスクバッファでは、満杯時の物理メモリの増加量がメモリバッファより 25～40 MB 少なかった。
- iOS ではビットレートの指定に従って保持時間が変化したが、Android では 1 Mbps と 2.5 Mbps (既定) の指定で保持時間が変わらなかった。Android でエンコードサイズを変更した場合に保持時間が変化したのは、エンコーダーが実際に生成するビットレートが変化したためである。

### v1.7.0 以降の変更

v1.7.0 の後に次の変更が行われた。その効果を、Galaxy S22 上で上記と同じ条件により、既定の設定とバッファ 60 MiB の設定で計測した。iOS では再計測していない。

- エンコード済みデータのバッファは、2 のべき乗に切り上げた配列ではなく、`MaxMemoryUsageBytesForCompressedFrames` 以内の 64 KiB のチャンクにフレームを格納する。
- セッションが保持するメモリは、エクスポートを行った場合も、セッションを破棄した時点で解放される。
- エクスポート時に、多重化とローカルタイムゾーンの初回の初期化が main thread 上で行われなくなった。
- Android では、ネイティブライブラリのスレッドの CPU 使用量が減少した。MediaCodec の待機を 1 回ずつスレッドプールに渡す処理をなくし、エンコーダーの入力サーフェスごとに作成する Vulkan のオブジェクトを再利用し、Java のメソッドの検索結果をキャッシュするようにした。

| 項目 | 条件 | v1.7.0 | 変更後 |
| --- | --- | --- | --- |
| GC Used Memory 満杯時 (MB) | 既定 | +38～+39 | +23～+24 |
| GC Used Memory 満杯時 (MB) | バッファ 60 MiB | +108 | +70～+71 |
| 物理メモリ 満杯時 (MB) | 既定 | +66～+71 | +47～+60 |
| 物理メモリ 満杯時 (MB) | バッファ 60 MiB | +151～+153 | +117～+120 |
| セッション破棄後の GC Used Memory (MB) | 既定 | 38～39 | 2.5～2.6 |
| エクスポート中の最長のフレーム (ms) | 既定 | 100～117 | 33～50 |

録画なしの状態の GC Used Memory は約 2.1 MB であった。CPU 使用量は、同じアプリケーションのネイティブライブラリのみを置き換えて simpleperf で計測した。録画中のネイティブライブラリのスレッドの CPU 時間は、約 195 ms/s から約 120 ms/s に減少した。プロセス全体の CPU 使用量は、計測ごとのばらつきがこの差より大きく、別の計測では約 50 ms/s 減少した。
