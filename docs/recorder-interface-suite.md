# Recorder Interface Suite

This document describes the design of the recorder interface suite, a set of runtime components and user interfaces that is shipped inside the InstantReplay package.

## Motivation

The C# API of InstantReplay covers sessions and exports, and most projects build their own integration on top of it. The suite provides the functionality that such integrations commonly need, so that a project can adopt InstantReplay with little or no additional code.

## Scope

The suite provides the following:

- A runtime user interface built with UI Toolkit, shown in Japanese or English.
- Enabling and disabling of continuous (buffered) recording.
- Recording settings: resolution, frame rate, bitrate, buffer size with an estimate of the recordable duration, and `ForceReadback`. Projects define presets, and the user selects one of them at runtime and can customize it.
- Starting, pausing and stopping unbounded recording.
- An always-visible overlay that shows the recorder state, the buffered duration and the buffer capacity.
- Browsing, previewing, deleting and sharing of recorded videos. Sharing is abstracted so that Slack, Discord, file export and other destinations can be supported.
- A recovery interface for sessions left behind by disk-buffered recording.
- Trimming of a replay before it is saved, and selection of a thumbnail frame.
- An API through which the project selects the frame provider and the audio sample provider.
- An API through which the application drives the recorder, the trim view and a recording picker from its own screens, without the overlay or the panel.

The following are out of scope:

- Sharing through the operating system share sheet on mobile platforms, which requires native code. Third-party plugins can be integrated through the sharing abstraction.
- Trimming of MP4 files that have already been written, which requires a demuxer.
- Excluding the suite's own user interface from the recording. See **Frame Provider Default**.

## Summary of Decisions

| Topic | Decision |
|---|---|
| Distribution | The suite is part of the package, not a UPM sample. |
| API stability | All public API of the suite is treated as stable API under semantic versioning. |
| Layering | A UI-independent control layer and a UI Toolkit view layer are separate assemblies. |
| Frame provider default | `ScreenshotFrameProvider`. The suite's user interface therefore appears in recordings. Projects that need to exclude it supply a different frame provider. |
| Provider lifetime | The suite holds at most one instance of each provider and reuses it across sessions. |
| Buffered and unbounded recording | Mutually exclusive. Starting an unbounded recording stops buffered recording, and buffered recording resumes after the unbounded recording ends. |
| Trimming | Implemented without changes to the native library. The encoded frames are captured from the buffer, a preview MP4 is written and played with `VideoPlayer`, and the trimmed MP4 is muxed from the captured frames. |
| Fonts | No font is bundled. Japanese text is rendered with a font installed on the device. |

## Distribution

The suite is distributed inside the package rather than as a UPM sample. A sample is copied into the project's `Assets` folder on import and must be re-imported on every package update, which leaves projects with outdated copies. Code inside the package is updated together with the package.

As a consequence, any breaking change to the suite's public API breaks projects on update. The suite's public surface is therefore kept small and is designed according to the policy in **API Stability Policy**.

## Assembly Layout

| Assembly | Contents | References |
|---|---|---|
| `InstantReplay` (existing) | Sessions and pipelines | — |
| `InstantReplay.Suite` | Recorder control, presets, settings asset, recording library, sharing abstraction, file export share target. No dependency on any UI framework. | `InstantReplay` |
| `InstantReplay.Suite.UI` | The `InstantReplaySuite` component, UI Toolkit views, USS and theme style sheet | `InstantReplay.Suite` |
| `InstantReplay.Suite.Sharing.Slack` | Slack share target. Compiled only when the UnityWebRequest module is enabled. | `InstantReplay.Suite` |
| `InstantReplay.Suite.Sharing.Discord` | Discord share target. Compiled only when the UnityWebRequest module is enabled. | `InstantReplay.Suite` |

Every assembly of the suite has the define constraint `!EXCLUDE_INSTANTREPLAY`, in the same way as the existing assemblies. The package declares no dependency on built-in modules; optional functionality is enabled through `versionDefines`:

- The sharing assemblies define `INSTANTREPLAY_UNITYWEBREQUEST` from the UnityWebRequest module and require it as a define constraint.
- The view layer defines `INSTANTREPLAY_VIDEO` from the Video module and omits the preview without it.
- The view layer defines `INSTANTREPLAY_IMAGECONVERSION` from the Image Conversion module and omits thumbnail capture and display without it.

Projects that build their own user interface can reference only `InstantReplay.Suite`. Because the views are not part of the public API, they can be redesigned without breaking such projects.

Assets of the view layer (USS and the theme style sheet) are referenced from serialized fields, not loaded through `Resources`, so that they are included in a build only when the suite is used.

## API Stability Policy

1. Extension points are abstract classes rather than interfaces. A virtual member with a default implementation can be added to an abstract class without breaking existing derived classes. This applies to `ShareTarget`.
2. Methods that are expected to gain parameters take a request object and a context object, for example `ShareAsync(ShareRequest request, ShareContext context, CancellationToken cancellationToken)`. New fields can then be added without changing the method signature.
3. View classes are `internal`. Names of elements and USS classes are not part of the public contract.
4. Custom UXML elements are not exposed. Unity 2022.3, the minimum supported version, lacks `[UxmlElement]` and runtime data binding, which were introduced in Unity 2023.2, while `UxmlFactory` and `UxmlTraits` are deprecated in Unity 6. Views are composed of built-in controls and constructed from C#.
5. Serialized data is treated as part of the API. Serialized fields of `ScriptableObject` types are private and exposed through public properties, and renamed fields carry `FormerlySerializedAs`. JSON files written by the suite (user settings, recording metadata) contain a format version.

## Components

### Recording Presets

A preset is defined by the project and describes one set of recording settings.

```csharp
[Serializable]
public sealed class RecordingPreset
{
    public string Id { get; set; }
    public string DisplayName { get; set; }
    public ResolutionSpec Resolution { get; set; }  // fixed size, or a ratio of the screen size with an upper bound on the longer side
    public BitrateSpec VideoBitrate { get; set; }   // fixed value, or computed from bits per pixel
    public int FrameRate { get; set; }
    public BufferSpec Buffer { get; set; }          // in-memory size, or disk buffer options
    public bool ForceReadback { get; set; }
    public int AudioBitrate { get; set; }

    public RealtimeEncodingOptions ToEncodingOptions(in PresetContext context);
    public double EstimateRecordableDurationSeconds(in PresetContext context);
}
```

Each property is backed by a private serialized field, so that presets can be edited in the inspector and constructed from code. `ResolutionSpec`, `BitrateSpec` and `BufferSpec` are serializable structs created through static factory methods such as `ResolutionSpec.ScreenRelative(scale, maxLongSide)` and `BufferSpec.Disk(maxMegabytes)`. The resolved width and height are rounded to even numbers of at least 2.

`PresetContext` carries runtime values that a preset depends on: the screen size and the audio sample rate. `PresetContext.FromCurrentEnvironment` uses `AudioSettings.outputSampleRate`.

`EstimateRecordableDurationSeconds` divides the buffer size in bits by the sum of the video and audio bitrates. Because the encoder treats the bitrate as a target rather than a limit, the user interface presents the value as an estimate.

### Settings Asset

`InstantReplaySuiteSettings` is a `ScriptableObject` that holds project-level configuration: the list of presets, the default preset, whether buffered recording starts automatically, the output directory of the recording library (relative paths are resolved against `Application.persistentDataPath`), whether the user's selections are persisted, the retention policy, and the share targets.

Selections made by the user at runtime, such as the selected preset, a customized preset and whether buffered recording is enabled, are stored separately as a versioned JSON file under `Application.persistentDataPath`.

### Bootstrapping

The suite is started by adding the `InstantReplaySuite` component to a game object in the first scene. No prefab is provided, because a prefab saved in a newer Unity version may fail to load in Unity 2022.3. The component creates its `PanelSettings` and `UIDocument` at runtime. Its references to the theme style sheet and the style sheet of the package are assigned in the editor from fixed package paths. Automatic startup through `RuntimeInitializeOnLoadMethod` is not provided, because locating the settings asset at startup would require `Resources`.

Only one `InstantReplaySuite` can be active at a time, and `InstantReplaySuite.Current` returns it. A second instance destroys itself, because a second recorder would create a second set of providers.

`InstantReplaySuite.Recorder` is created on first access. Providers configured on it before the component's `Start` replace the default providers before any of them is created. `Start` calls `InstantReplayRecorder.Activate`, which restores the user's selections and starts buffering when enabled.

The component has the following serialized settings: the settings asset, the sorting order of the panel, whether the game object survives scene loads, whether the overlay is shown at launch, whether tapping the overlay opens the panel, the corner of the screen where the overlay is placed, the language, and a scale factor.

### Recorder

`InstantReplayRecorder` is the UI-independent controller. The view layer depends only on this type and the types it exposes.

```csharp
public sealed class InstantReplayRecorder : IDisposable
{
    public InstantReplayRecorder(InstantReplaySuiteSettings settings);
    public void Activate();

    public InstantReplaySuiteSettings Settings { get; }
    public RecorderState State { get; }  // Idle, Buffering, Exporting, RecordingUnbounded, Faulted
    public event Action<RecorderState> StateChanged;
    public event Action ConfigurationChanged;  // preset, buffering toggle, pause
    public Exception LastException { get; }
    public IReadOnlyList<RecordingPreset> Presets { get; }
    public RecordingPreset ActivePreset { get; }
    public bool IsActivePresetCustomized { get; }
    public bool IsBufferingEnabled { get; }
    public bool IsPaused { get; }
    public RecorderStatistics GetStatistics();  // buffered duration, estimated capacity, bytes used and capacity, unbounded elapsed time

    public void SetFrameProviderFactory(Func<IFrameProvider> factory);
    public void SetFrameProvider(IFrameProvider provider, bool takeOwnership);
    public void SetAudioSampleProviderFactory(Func<IAudioSampleProvider> factory);
    public void SetAudioSampleProvider(IAudioSampleProvider provider, bool takeOwnership);
    public void InvalidateFrameProvider();
    public void InvalidateAudioSampleProvider();

    public void SetBufferingEnabled(bool enabled);
    public void ApplyPreset(RecordingPreset preset);
    public void ApplyPreset(string presetId);
    public void Pause();
    public void Resume();
    public ValueTask<RecordingEntry> SaveReplayAsync(ReplaySaveRequest request = null);
    public ValueTask<ReplayDraft> CaptureReplayAsync(ReplaySaveRequest request = null);
    public UnboundedRecording StartUnbounded(UnboundedRecordingRequest request = null);
    public UnboundedRecording CurrentUnboundedRecording { get; }
    public RecordingLibrary Library { get; }
    public IList<ShareTarget> ShareTargets { get; }
}
```

Pausing is not a state. It is orthogonal to buffering and unbounded recording, and is exposed as `IsPaused`. Every member must be called on the main thread, and every event is raised on it. `ReplaySaveRequest` specifies the duration to keep and the metadata of the recording, and `UnboundedRecordingRequest` specifies the metadata. `UnboundedRecording` exposes the output path, the start time, the elapsed time excluding pauses, and `StopAsync`, whose result is memoized.

The recorder observes the following rules:

- While the application is suspended, as reported by `OnApplicationPause`, the recorder pauses the running session, because the providers keep their clocks running during the suspension and the suspension would otherwise appear as a long period without frames. The recorder observes the suspension through a hidden game object created by `Activate`, so that it also works without the component. The pause caused by the suspension is tracked separately from `Pause`, does not change `IsPaused`, and does not resume a recording that the user has paused.
- Buffered recording and unbounded recording are mutually exclusive. `StartUnbounded` disposes the buffered session before it starts the unbounded session. When the unbounded recording ends, buffered recording resumes if it is enabled.
- Encoder options are fixed when a session is created, so `ApplyPreset` disposes the current session and creates a new one. The buffered footage is discarded.
- Saving or capturing a replay ends the buffered session. Nothing is recorded between the start of the operation and the creation of the next session.
- When a session reports an exception through `onException`, the recorder disposes the session, invalidates the owned providers, and creates a new session. When three failures occur within ten seconds, the recorder enters `Faulted` and stops retrying. Enabling buffering again clears the fault and retries.
- Changing the preset or a provider throws `InvalidOperationException` while a replay is being saved or an unbounded recording is running. Enabling or disabling buffering in these states takes effect when the operation finishes.
- A failed export or a failed unbounded recording deletes its incomplete output file. Disposing the recorder during an unbounded recording also deletes the output file, because the file cannot be finalized synchronously.

### Provider Lifetime

Some providers allow only one instance at a time. `CriAudioSampleProvider` attaches a `CriAtomExOutputAnalyzer` to a DSP bus, and a DSP bus accepts only one analyzer. A factory that creates a new provider for every session would create a second instance whenever the new session is created before the old one is disposed, or whenever the application already holds its own instance.

The provider lifetime is therefore decoupled from the session lifetime:

- The recorder holds at most one instance of each provider. The factory is invoked only when no live instance exists.
- The same instance is passed to every session that the recorder creates, including sessions created by `ApplyPreset` and by switching between buffered and unbounded recording. Sessions receive the instance with `disposeFrameProvider: false` and `disposeAudioSampleProvider: false`, and unsubscribe from it when they are disposed.
- The recorder disposes an owned instance only when the recorder itself is disposed or when the factory or instance is replaced. On replacement, the old instance is disposed before the new instance is created.
- `SetFrameProvider` and `SetAudioSampleProvider` accept an instance created by the application. When `takeOwnership` is false, the recorder never disposes the instance, so the application can share a provider such as `CriAudioSampleProvider` with its own code.
- `InvalidateFrameProvider` and `InvalidateAudioSampleProvider` dispose the owned instance and cause the factory to be invoked again for the next session. An instance supplied by the application is kept and only resubscribed. They are used when the provider can no longer operate, for example when its camera has been destroyed.
- Factories take no arguments. A context parameter can be added later as an overload without breaking existing callers.

### Frame Provider Default

When no frame provider is configured, the recorder uses `ScreenshotFrameProvider`. `ScreenCapture` includes UI Toolkit panels rendered as overlays, so the overlay and the suite's panels appear in recordings. Projects that need to exclude them supply a camera-based frame provider such as `BuiltinCameraFrameProvider` or `RendererFeatureFrameProvider`.

### Recording Library

`RecordingLibrary` manages the videos saved by the suite in the configured output directory. Each video is described by a `RecordingEntry`:

- Identifier, file path, creation time, duration and file size.
- `Source`: `Replay`, `Unbounded` or `Recovered`.
- `Metadata`: a dictionary of strings.
- Path of the thumbnail image, if one exists.

The entry is stored as a JSON file next to the MP4 file. The thumbnail is stored as a PNG file with the same file name, and the JSON file records its file name. The library provides listing, deletion of one or several entries, import of recovered sessions, `SetThumbnail` and the `Changed` event. Deleting several entries raises `Changed` once. A file that is still being written, by a replay export, an unbounded recording or a recovery import, is excluded from the listing and from retention until it is completed.

The library applies the retention policy from the settings asset (`MaxRecordings` and `MaxTotalMegabytes`, where zero means unlimited) whenever an entry is added. The oldest entries are deleted first. The entry just added is always kept, even when it alone exceeds the total size, because deleting a recording the user has just saved would be unexpected.

### Sharing

```csharp
public abstract class ShareTarget : ScriptableObject
{
    public virtual string DisplayName { get; set; }
    public virtual bool CanShare(ShareRequest request, out string reason);
    public abstract ValueTask<ShareResult> ShareAsync(ShareRequest request, ShareContext context, CancellationToken cancellationToken);
}
```

`ShareTarget` derives from `ScriptableObject` so that each destination is an asset configured in the inspector and listed in `InstantReplaySuiteSettings.ShareTargets`. Serializing a polymorphic list with `SerializeReference` was rejected because Unity 2022.3 provides no inspector for choosing the concrete type. A target can also be created with `ScriptableObject.CreateInstance` and added to `InstantReplayRecorder.ShareTargets` at runtime, which is initialized from the settings asset.

`ShareRequest` contains the entries to share and a comment entered by the user; the metadata is available through the entries. `ShareContext` provides progress reporting. `CanShare` returns a reason that the user interface shows when sharing is not possible, for example when the files exceed a size limit or credentials are missing. `ShareAsync` returns a `ShareResult` with a message and an optional URL, and reports failure by throwing; the message of the exception is shown to the user. Targets are called on the main thread.

The package provides the following targets:

- File export (`FileExportShareTarget`, in `InstantReplay.Suite`): copies the video to a configured directory. Relative paths are resolved against `Application.persistentDataPath`. It can optionally open the directory afterwards on desktop platforms.
- Slack (`SlackShareTarget`): uploads through `files.getUploadURLExternal` and `files.completeUploadExternal` with a bot token that has the `files:write` scope. The file is streamed from disk with `UploadHandlerFile`.
- Discord (`DiscordShareTarget`): posts through a webhook as a multipart request. Discord limits the total size of the attachments of a message according to the boost level of the server, so the limit is configurable (10 MiB by default), and `CanShare` rejects requests above it or with more than ten files.

The Slack and Discord targets can append the metadata of the recordings to the message. Tokens and webhook URLs for Slack and Discord are embedded in the build when configured in assets. The documentation states that these targets are intended for development and quality-assurance builds, and the targets also accept credentials supplied from code at runtime.

### Recovery

The recovery interface lists sessions returned by `DiskEncodedFrameBufferRecovery.FindRecoverable`, and allows the user to import a session into the recording library or to delete it. `RecordingLibrary.ImportRecoveredAsync` exports a session into the library with `Source` set to `Recovered` and the session start time as the creation time, and leaves the session directory in place, in line with the policy of the core package that recovery never deletes implicitly. The user interface deletes the session directory after a successful import, because the footage then exists in the library. The duration of a recovered entry is recorded as unknown.

### Application-Driven Operation

Every operation of the panel is available from code, so that an application can drive the recorder from its own screens, for example a bug report screen in a debug menu.

- `InstantReplaySuite.IsOverlayVisible` hides or shows the overlay. The recorder keeps running while the overlay is hidden.
- `InstantReplaySuite.OverlayOpensPanel` controls whether tapping the overlay opens the panel. When it is false, the overlay only shows the recording status and taps pass through it.
- `InstantReplaySuite.OverlayCorner` places the overlay at one of the four corners of the screen. Setting it from code does not overwrite the corner chosen by the user.
- `InstantReplaySuite.TrimAsync(ReplayDraft, CancellationToken)` shows the trim view for a draft and returns the `ReplaySelection` chosen by the user, or null when the user cancels. Nothing is saved. The draft stays with the caller, which writes the footage with `ReplayDraft.ExportAsync(ReplaySelection, string)` or `ReplayDraft.SaveAsync(ReplaySelection)`.
- `InstantReplaySuite.PickRecordingsAsync(bool, CancellationToken)` shows the recordings in the library together with the sessions left behind by abnormal terminations, and returns the recordings chosen by the user. Chosen sessions are recovered into the library first.

Both views are shown even while the overlay is hidden. They throw `InvalidOperationException` when another view of the suite is open, and close when the cancellation token is cancelled.

### User Interface

- The suite uses its own `PanelSettings` with a high sorting order and the Constant Physical Size scale mode, and respects the safe area. One unit of the style sheet corresponds to 1/96 inch on desktops, following the CSS pixel, and to 1/160 inch on handheld devices, following the Android density-independent pixel and close to the iOS point. The `scale` field of `InstantReplaySuite` multiplies the size. The device type, the screen size and the safe area are read through `UnityEngine.Device`, so that the Device Simulator is honored.
- The package ships its own theme style sheet that imports `unity-theme://default`, because the default runtime theme is generated in the project's `Assets` folder and cannot be referenced from the package.
- The text is shown in Japanese or English. `SuiteLanguage.Automatic` selects Japanese when the system language is Japanese and English otherwise.
- No font is bundled. Unity 6 falls back to the fonts of the operating system by itself. Unity 2022.3 does not, and the font of the default theme has no Japanese characters, so on Unity 2022.3 the suite creates a dynamic font asset from the first available font among a list of Japanese system fonts (Hiragino on Apple platforms, Noto Sans CJK on Android, Yu Gothic, Meiryo or MS Gothic on Windows) and sets it as the fallback of the panel's text settings.
- Controls respond to taps on release. A touch that moves far enough to be a drag scrolls the enclosing scroll view instead of activating the control. Text fields take the focus, and open the touch screen keyboard, on the release of a tap. A touch that stops a scrolling panel over a text field does not focus it.
- The overlay is always visible while enabled. It shows the recorder state, the buffered duration against the estimated capacity, and the elapsed time of an unbounded recording. Tapping the overlay opens the main panel unless `OverlayOpensPanel` is false. The panel opens next to the overlay, below it at the top corners and above it at the bottom corners. The user can move the overlay to another corner with a dropdown in the panel. When `PersistUserPreferences` is true, the choice is stored in `suite-ui-preferences.json` under `Application.persistentDataPath/InstantReplay`, separately from the preferences of the recorder, and restored at the next launch.
- The panel has two tabs. The recorder tab contains the buffering toggle, the Save replay button, the unbounded recording controls, the preset selection and the preset editor. The recordings tab lists the recordings in the library, newest first, with the recovery interface above the list when recoverable sessions exist.
- The preset editor allows the user to override individual properties of the active preset. A change applies a modified copy of the active preset through `ApplyPreset(RecordingPreset)`, and `InstantReplayRecorder.IsActivePresetCustomized` reports that the active preset is not one of the presets in the settings asset. A customized preset is stored in the user settings and restored at the next launch in preference to the selected preset identifier.
- Selecting a recording shows its preview, its metadata, a button that takes the current frame of the preview as the thumbnail, deletion, and sharing with a choice of target and a comment. Deletion requires a second tap within three seconds.
- The recordings tab has a selection mode. In this mode each row has a check box, a toggle selects all recordings, and the selected recordings are deleted together after a second tap. Each row shows the thumbnail when one exists.
- The Save replay button captures the buffer with `CaptureReplayAsync` and opens the trim view, in which the whole footage is selected initially. After saving, the panel switches to the recordings tab. `SaveReplayAsync` saves without user interaction.
- The trim view contains the preview, a timeline, buttons that set the start or the end at the playback position, a button that takes the current frame as the thumbnail, and buttons that save or discard the replay. The timeline shows the selected range with a handle at each end, the playback position with a knob, and a tick at every key frame. A drag on a handle changes the range, and the start moves to the key frame where the saved recording actually starts. A drag on the knob or a click elsewhere on the timeline moves the playback position within the range, and playback pauses during the drag. Playback loops within the selected range. On a short screen the preview shrinks first, and the controls scroll once the preview has reached its minimum height.
- Recorded videos are previewed with `VideoPlayer` rendering into a `RenderTexture`. Preview is not available on WebGL, where `VideoPlayer` cannot open local files.

## Sample

The `User Interfaces` sample uses the suite. Its scene contains an `InstantReplaySuite` component with a settings asset that defines three presets (`low`, `standard` and `long-disk`, the last of which uses the disk buffer).

## Changes to the Core Package

1. Buffer statistics. `RealtimeInstantReplaySession.GetBufferStatistics` returns a `RecordingBufferStatistics` with the duration from the earliest retained video key frame to the latest video frame, the bytes used, and the capacity. The overlay depends on it.
2. Exclusion of live sessions from recovery. `DiskEncodedFrameBuffer` registers its session directory in a process-wide set while it is open, and `DiskEncodedFrameBufferRecovery.FindRecoverable` does not return registered directories, so that a recovery interface that deletes every listed session does not remove the active recording. Sessions written by other processes are not excluded, which is acceptable because the recovery interface runs in the process that owns the disk buffer.
3. Capture of encoded frames for trimming. `RealtimeInstantReplaySession.StopAndCaptureAsync` returns a `CapturedReplay`. See **Trimming**.

## Trimming

Trimming is implemented without changes to the native library.

1. The recorder stops the buffered session and captures the encoded frames covering the buffered duration as a disposable object. How the frames are held depends on the buffer:
   - With the in-memory buffer, the object holds the encoded frames in memory. Their total size is bounded by `MaxMemoryUsageBytesForCompressedFrames`.
   - With the disk buffer, the object holds only the location and timestamp of every frame in the segment files, and keeps the session directory until it is disposed. Payloads are read from the segment files while muxing.
2. All captured frames are muxed into a temporary preview MP4 file. `VideoPlayer` accepts only a `VideoClip` or a URL, so a file is required for preview.
3. The user plays the preview and selects the start and end points. The key frame timestamps are displayed on the timeline as the candidate start points. Because the frames are rebased so that each stream starts at zero, the timestamps correspond directly to the playback time of the preview.
4. The trimmed MP4 file is muxed from the captured frames for the selected range.

The core API is as follows.

```csharp
public sealed class CapturedReplay : IDisposable
{
    public double DurationSeconds { get; }
    public IReadOnlyList<double> KeyFrameTimestamps { get; }
    public int Width { get; }
    public int Height { get; }
    public double GetExportStart(double seconds);
    public ValueTask<string> ExportAsync(double startSeconds = 0, double? endSeconds = null, string outputPath = null);
}
```

- `StopAndCaptureAsync` ends the session in the same way as `StopAndExportAsync` and can be called only once. The returned object remains valid after the session is disposed.
- `DurationSeconds` is the time of the latest video frame, not of the last one. MediaCodec on Android may end the stream with a frame at time zero, which the muxer drops.
- An export starts at the latest key frame at or before the requested start and ends before the first video frame at or after the requested end. Audio frames are selected for the same time range. A range that contains no video frame throws `ArgumentException`.
- An export can be repeated any number of times over any range.
- Members are thread-safe. Disposal during an export defers the release of the frames until the export finishes.
- With the disk buffer, the session directory remains registered as a live session until the object is disposed, so recovery does not offer it. If the application terminates before disposal, the directory becomes recoverable.

The suite wraps the capture in `ReplayDraft`, which `InstantReplayRecorder.CaptureReplayAsync` returns.

```csharp
public sealed class ReplayDraft : IDisposable
{
    public DateTime CapturedAtUtc { get; }
    public double DurationSeconds { get; }
    public IReadOnlyList<double> KeyFrameTimestamps { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
    public bool IsDisposed { get; }
    public double GetSaveStart(double seconds);
    public ValueTask<string> GetPreviewPathAsync();
    public ValueTask<string> ExportAsync(double startSeconds = 0, double? endSeconds = null, string outputPath = null);
    public ValueTask<string> ExportAsync(ReplaySelection selection, string outputPath = null);
    public ValueTask<RecordingEntry> SaveAsync(double startSeconds = 0, double? endSeconds = null, byte[] thumbnailPng = null);
    public ValueTask<RecordingEntry> SaveAsync(ReplaySelection selection);
}

public sealed class ReplaySelection
{
    public ReplaySelection(double startSeconds, double? endSeconds = null, byte[] thumbnailPng = null);
    public double StartSeconds { get; }
    public double? EndSeconds { get; }  // null means the end of the draft
    public byte[] ThumbnailPng { get; }
}
```

- The recorder resumes buffering as soon as the capture completes, so footage continues to be buffered while the user trims, and no dedicated recorder state is required. The draft and the new buffer coexist, so the memory or disk usage can reach twice the buffer capacity while a draft is open.
- `GetPreviewPathAsync` writes the whole capture to the temporary cache directory once and returns the same path on later calls. The file is deleted when the draft is disposed.
- `ExportAsync` writes a range to a file outside the library, which the caller owns. `SaveAsync` adds a `Replay` entry to the library. Both can be called any number of times.

Muxing from the captured frames avoids the need for a demuxer, which the native library does not provide. For the same reason, MP4 files that have already been written, including library entries and recovered sessions, cannot be trimmed.

The granularity of trimming equals the key frame interval of the platform encoder, which is about one second on macOS. Pull request #172 makes the IDR interval configurable through `VideoEncoderOptions.IdrIntervalSeconds`; the suite does not depend on it, and a corresponding preset property can be added without a breaking change.

On Apple platforms, files written by the muxer without the fix in pull request #172 lack the sync sample table (`stss`), and `VideoPlayer` then fails to seek in the preview. The suite does not work around this.

### Thumbnails

A thumbnail is extracted from the frame that `VideoPlayer` is showing. The frame is copied from the `RenderTexture` into a smaller temporary texture whose longer side is at most 320 pixels, read back into a `Texture2D` and encoded as PNG.

## Open Issues

- Sample rate of presets for fixed-rate audio sample providers. `AudioTemporalAdjuster` converts the sample rate and the channel count reported by the provider to those of `AudioOptions`, so a mismatch does not break recording. The conversion, however, picks the nearest preceding input sample without interpolation or filtering, which introduces aliasing. In addition, the output length of each callback is rounded independently, so the accumulated rounding error can reach the lag threshold (0.1 seconds by default) and cause a sample skip or an inserted blank. Because a provider may change its sample rate during recording, this is to be solved by proper resampling in the core package rather than by presets. It is out of the scope of this suite.
- Estimated capacity. Once five seconds have been buffered, the estimate is derived from the bytes actually used per second rather than from the configured bitrates. A static scene compresses far below the target bitrate, so the estimate can exceed what a busier scene would allow.
- Metadata providers. A project cannot yet register a provider that attaches information such as the build number or the active scene to every new recording; metadata is passed through the request objects.
