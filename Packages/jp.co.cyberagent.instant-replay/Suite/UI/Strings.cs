// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Globalization;
using UnityEngine;

namespace InstantReplay.Suite.UI
{
    /// <summary>Language of the text shown by <see cref="InstantReplaySuite" />.</summary>
    public enum SuiteLanguage
    {
        /// <summary>Japanese when the system language is Japanese, and English otherwise.</summary>
        Automatic,
        English,
        Japanese
    }

    /// <summary>
    ///     Text shown by the suite in one language. The views read <see cref="Current" /> when they are built.
    /// </summary>
    internal sealed class Strings
    {
        public static readonly Strings English = new()
        {
            OverlayPausedSuffix = " (paused)",
            OverlayRecording = "REC {0}",
            OverlaySaving = "Saving…",
            OverlayError = "Error",
            OverlayOff = "Off",

            RecorderTab = "Recorder",
            RecordingsTab = "Recordings",
            ContinuousBuffering = "Continuous recording",
            SaveReplay = "Save replay",
            StartRecording = "Start recording",
            StopRecording = "Stop recording",
            Pause = "Pause",
            Resume = "Resume",
            Preset = "Preset",
            PresetDetails = "{0}×{1} @ {2} fps, {3:0.0} Mbps\nBuffer: {4} MiB {5} (≈ {6})",
            BufferInMemory = "in memory",
            BufferOnDisk = "on disk",
            RecordingFailed = "Recording failed.",
            OverlayPosition = "Overlay position",
            TopLeft = "Top left",
            TopRight = "Top right",
            BottomLeft = "Bottom left",
            BottomRight = "Bottom right",

            Customize = "Customize",
            Resolution = "Resolution",
            ScreenRelative = "Screen relative",
            FixedSize = "Fixed size",
            Scale = "Scale",
            MaxLongSide = "Max long side",
            Width = "Width",
            Height = "Height",
            FrameRate = "Frame rate",
            VideoBitrate = "Video bitrate",
            BitsPerPixel = "Bits per pixel",
            FixedBitrate = "Fixed",
            Mbps = "Mbps",
            AudioKbps = "Audio kbps",
            Buffer = "Buffer",
            Memory = "Memory",
            Disk = "Disk",
            BufferMegabytes = "Buffer MiB",
            ForceReadback = "Force readback",
            ResetToPreset = "Reset to preset",
            CustomizedPreset = "{0} (custom)",

            All = "All",
            Select = "Select",
            Done = "Done",
            Delete = "Delete",
            DeleteCount = "Delete {0}",
            ConfirmDelete = "Tap again to confirm",
            NoRecordings = "No recordings yet.",
            BackToRecordings = "‹ Recordings",
            UseFrameAsThumbnail = "Use frame as thumbnail",
            ShareTo = "Share to",
            Comment = "Comment",
            Share = "Share",
            Open = "Open",
            Cancel = "Cancel",
            PreviewFailed = "Preview failed: {0}",
            DeletionFailed = "Deletion failed: {0}",
            ThumbnailUpdated = "Thumbnail updated.",
            ThumbnailFailed = "Thumbnail failed: {0}",
            DestinationCannotAccept = "This destination cannot accept the recording.",
            SharingTo = "Sharing to {0}…",
            SharingProgress = "Sharing to {0}… {1:0}%",
            SharedTo = "Shared to {0}.",
            SharingCancelled = "Sharing cancelled.",
            SharingFailed = "Sharing failed: {0}",

            SourceReplay = "Replay",
            SourceUnbounded = "Recording",
            SourceRecovered = "Recovered",

            ChooseRecordings = "Choose recordings",
            ChooseRecording = "Choose a recording",
            Use = "Use",
            UseCount = "Use {0}",
            Unfinished = "Unfinished · {0}",
            RecordedBy = "Recorded by {0} {1}",
            Recovering = "Recovering…",
            RecoveryFailed = "Recovery failed: {0}",
            UnfinishedRecordings = "Unfinished recordings",
            UnfinishedRecordingsDescription = "Footage buffered on disk before the application ended unexpectedly.",
            Recover = "Recover",
            Recovered = "Recovered into the library.",

            TrimReplay = "Trim replay",
            SetStart = "Set start",
            SetEnd = "Set end",
            Play = "Play",
            Discard = "Discard",
            Save = "Save",
            PreparingPreview = "Preparing preview…",
            Saving = "Saving…",
            SavingFailed = "Saving failed: {0}"
        };

        public static readonly Strings Japanese = new()
        {
            OverlayPausedSuffix = "（一時停止中）",
            OverlayRecording = "REC {0}",
            OverlaySaving = "保存中…",
            OverlayError = "エラー",
            OverlayOff = "オフ",

            RecorderTab = "レコーダー",
            RecordingsTab = "録画一覧",
            ContinuousBuffering = "常時録画",
            SaveReplay = "リプレイを保存",
            StartRecording = "録画開始",
            StopRecording = "録画停止",
            Pause = "一時停止",
            Resume = "再開",
            Preset = "プリセット",
            PresetDetails = "{0}×{1} @ {2} fps, {3:0.0} Mbps\nバッファ: {4} MiB {5}（約 {6}）",
            BufferInMemory = "メモリ",
            BufferOnDisk = "ディスク",
            RecordingFailed = "録画に失敗しました。",
            OverlayPosition = "オーバーレイの位置",
            TopLeft = "左上",
            TopRight = "右上",
            BottomLeft = "左下",
            BottomRight = "右下",

            Customize = "カスタマイズ",
            Resolution = "解像度",
            ScreenRelative = "画面サイズ基準",
            FixedSize = "固定サイズ",
            Scale = "倍率",
            MaxLongSide = "長辺の上限",
            Width = "幅",
            Height = "高さ",
            FrameRate = "フレームレート",
            VideoBitrate = "映像ビットレート",
            BitsPerPixel = "Bits per pixel",
            FixedBitrate = "固定",
            Mbps = "Mbps",
            AudioKbps = "音声 kbps",
            Buffer = "バッファ",
            Memory = "メモリ",
            Disk = "ディスク",
            BufferMegabytes = "バッファ MiB",
            ForceReadback = "Readback を強制",
            ResetToPreset = "プリセットに戻す",
            CustomizedPreset = "{0}（カスタム）",

            All = "すべて",
            Select = "選択",
            Done = "完了",
            Delete = "削除",
            DeleteCount = "{0} 件を削除",
            ConfirmDelete = "もう一度タップで確定",
            NoRecordings = "録画はまだありません。",
            BackToRecordings = "‹ 録画一覧",
            UseFrameAsThumbnail = "このフレームをサムネイルにする",
            ShareTo = "共有先",
            Comment = "コメント",
            Share = "共有",
            Open = "開く",
            Cancel = "キャンセル",
            PreviewFailed = "プレビューに失敗しました: {0}",
            DeletionFailed = "削除に失敗しました: {0}",
            ThumbnailUpdated = "サムネイルを更新しました。",
            ThumbnailFailed = "サムネイルの作成に失敗しました: {0}",
            DestinationCannotAccept = "この共有先はこの録画を受け付けません。",
            SharingTo = "{0} に共有中…",
            SharingProgress = "{0} に共有中… {1:0}%",
            SharedTo = "{0} に共有しました。",
            SharingCancelled = "共有をキャンセルしました。",
            SharingFailed = "共有に失敗しました: {0}",

            SourceReplay = "リプレイ",
            SourceUnbounded = "録画",
            SourceRecovered = "復元",

            ChooseRecordings = "録画を選択",
            ChooseRecording = "録画を 1 件選択",
            Use = "使用",
            UseCount = "{0} 件を使用",
            Unfinished = "未完了 · {0}",
            RecordedBy = "{0} {1} で録画",
            Recovering = "復元中…",
            RecoveryFailed = "復元に失敗しました: {0}",
            UnfinishedRecordings = "未完了の録画",
            UnfinishedRecordingsDescription = "アプリケーションが異常終了する前にディスクへバッファされていた映像です。",
            Recover = "復元",
            Recovered = "録画一覧に復元しました。",

            TrimReplay = "リプレイをトリミング",
            SetStart = "開始位置に設定",
            SetEnd = "終了位置に設定",
            Play = "再生",
            Discard = "破棄",
            Save = "保存",
            PreparingPreview = "プレビューを準備中…",
            Saving = "保存中…",
            SavingFailed = "保存に失敗しました: {0}"
        };

        public string All;
        public string AudioKbps;
        public string BackToRecordings;
        public string BitsPerPixel;
        public string BottomLeft;
        public string BottomRight;
        public string Buffer;
        public string BufferInMemory;
        public string BufferMegabytes;
        public string BufferOnDisk;
        public string Cancel;
        public string ChooseRecording;
        public string ChooseRecordings;
        public string Comment;
        public string ConfirmDelete;
        public string ContinuousBuffering;
        public string Customize;
        public string CustomizedPreset;
        public string Delete;
        public string DeleteCount;
        public string DeletionFailed;
        public string DestinationCannotAccept;
        public string Discard;
        public string Disk;
        public string Done;
        public string FixedBitrate;
        public string FixedSize;
        public string ForceReadback;
        public string FrameRate;
        public string Height;
        public string MaxLongSide;
        public string Mbps;
        public string Memory;
        public string NoRecordings;
        public string Open;
        public string OverlayError;
        public string OverlayOff;
        public string OverlayPausedSuffix;
        public string OverlayPosition;
        public string OverlayRecording;
        public string OverlaySaving;
        public string Pause;
        public string Play;
        public string PreparingPreview;
        public string Preset;
        public string PresetDetails;
        public string PreviewFailed;
        public string RecordedBy;
        public string RecorderTab;
        public string RecordingFailed;
        public string RecordingsTab;
        public string Recover;
        public string Recovered;
        public string Recovering;
        public string RecoveryFailed;
        public string ResetToPreset;
        public string Resolution;
        public string Resume;
        public string Save;
        public string SaveReplay;
        public string Saving;
        public string SavingFailed;
        public string Scale;
        public string ScreenRelative;
        public string Select;
        public string SetEnd;
        public string SetStart;
        public string Share;
        public string ShareTo;
        public string SharedTo;
        public string SharingCancelled;
        public string SharingFailed;
        public string SharingProgress;
        public string SharingTo;
        public string SourceRecovered;
        public string SourceReplay;
        public string SourceUnbounded;
        public string StartRecording;
        public string StopRecording;
        public string ThumbnailFailed;
        public string ThumbnailUpdated;
        public string TopLeft;
        public string TopRight;
        public string TrimReplay;
        public string Unfinished;
        public string UnfinishedRecordings;
        public string UnfinishedRecordingsDescription;
        public string Use;
        public string UseCount;
        public string UseFrameAsThumbnail;
        public string VideoBitrate;
        public string Width;

        private Strings()
        {
        }

        public static Strings Current { get; private set; } = English;

        public static void SetLanguage(SuiteLanguage language)
        {
            if (language == SuiteLanguage.Automatic)
                language = Application.systemLanguage == SystemLanguage.Japanese
                    ? SuiteLanguage.Japanese
                    : SuiteLanguage.English;
            Current = language == SuiteLanguage.Japanese ? Japanese : English;
        }

        public static string Format(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }

        public string Source(RecordingSource source)
        {
            return source switch
            {
                RecordingSource.Unbounded => SourceUnbounded,
                RecordingSource.Recovered => SourceRecovered,
                _ => SourceReplay
            };
        }
    }
}
