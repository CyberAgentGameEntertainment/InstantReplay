// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UIElements;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Lists the recordings in the library and shows one of them with a preview, deletion, and sharing. In selection
    ///     mode, several recordings can be deleted at once.
    /// </summary>
    internal sealed class LibraryView : IDisposable
    {
        private readonly HashSet<string> _checkedPaths = new(StringComparer.Ordinal);
        private readonly ConfirmButton _deleteButton;
        private readonly ConfirmButton _deleteCheckedButton;
        private readonly VisualElement _detail;
        private readonly Label _detailInfo;
        private readonly Label _emptyLabel;
        private readonly VisualElement _list;
        private readonly VisualElement _listSection;
        private readonly Label _listStatus;
        private readonly Button _openResultButton;
        private readonly PreviewPlayer _preview;
        private readonly InstantReplayRecorder _recorder;
        private readonly RecoveryView _recovery;
        private readonly Toggle _selectAllToggle;
        private readonly Button _selectButton;
        private readonly VisualElement _selectionBar;
        private readonly Button _shareButton;
        private readonly TextField _shareComment;
        private readonly VisualElement _shareSection;
        private readonly Label _shareStatus;
        private readonly DropdownField _shareTarget;
        private readonly List<Texture2D> _thumbnails = new();
        private readonly Button _thumbnailButton;

        private string _resultUrl;
        private string _shareMessage;
        private RecordingEntry _selected;
        private IReadOnlyList<RecordingEntry> _entries = Array.Empty<RecordingEntry>();
        private bool _selectionMode;
        private CancellationTokenSource _sharing;
        private bool _visible;

        public LibraryView(InstantReplayRecorder recorder, Transform previewParent)
        {
            _recorder = recorder;

            Root = new VisualElement();
            Root.AddToClassList("irs-library");

            _listSection = new VisualElement();
            Root.Add(_listSection);

            _recovery = new RecoveryView(recorder);
            _listSection.Add(_recovery.Root);

            var toolbar = new VisualElement();
            toolbar.AddToClassList("irs-panel__row");
            toolbar.AddToClassList("irs-library__toolbar");
            _selectionBar = new VisualElement();
            _selectionBar.AddToClassList("irs-library__selection");
            _selectAllToggle = new Toggle("All");
            _selectAllToggle.AddToClassList("irs-library__select-all");
            _selectAllToggle.RegisterValueChangedCallback(e => SetAllChecked(e.newValue));
            _deleteCheckedButton = new ConfirmButton("Delete", DeleteChecked);
            _deleteCheckedButton.AddToClassList("irs-library__delete-checked");
            _selectionBar.Add(_selectAllToggle);
            _selectionBar.Add(_deleteCheckedButton);
            toolbar.Add(_selectionBar);
            _selectButton = new Button(() => SetSelectionMode(!_selectionMode)) { text = "Select" };
            _selectButton.AddToClassList("irs-library__select");
            toolbar.Add(_selectButton);
            _listSection.Add(toolbar);

            _listStatus = new Label();
            _listStatus.AddToClassList("irs-panel__error");
            _listSection.Add(_listStatus);

            _list = new VisualElement();
            _list.AddToClassList("irs-library__list");
            _listSection.Add(_list);

            _emptyLabel = new Label("No recordings yet.");
            _emptyLabel.AddToClassList("irs-panel__details");
            _listSection.Add(_emptyLabel);

            _detail = new VisualElement();
            _detail.AddToClassList("irs-library__detail");
            Root.Add(_detail);

            var back = new Button(CloseDetail) { text = "‹ Recordings" };
            back.AddToClassList("irs-library__back");
            _detail.Add(back);

            _preview = new PreviewPlayer(previewParent);
            _preview.Error += message => _detailInfo.text += $"\nPreview failed: {message}";
            _preview.Image.RegisterCallback<ClickEvent>(_ => _preview.TogglePause());
            if (PreviewPlayer.IsSupported) _detail.Add(_preview.Image);

            _detailInfo = new Label();
            _detailInfo.AddToClassList("irs-panel__details");
            _detail.Add(_detailInfo);

            var actions = new VisualElement();
            actions.AddToClassList("irs-panel__row");
            actions.AddToClassList("irs-panel__section");
            _thumbnailButton = new Button(SetThumbnailFromPreview) { text = "Use frame as thumbnail" };
            if (PreviewPlayer.IsSupported && PreviewPlayer.CanCaptureFrame) actions.Add(_thumbnailButton);
            _deleteButton = new ConfirmButton("Delete", DeleteSelected);
            actions.Add(_deleteButton);
            _detail.Add(actions);

            _shareSection = new VisualElement();
            _shareSection.AddToClassList("irs-panel__section");
            _shareTarget = new DropdownField("Share to", new List<string>(), 0);
            _shareTarget.RegisterValueChangedCallback(_ => RefreshShare());
            _shareComment = new TextField("Comment") { multiline = true };
            _shareComment.AddToClassList("irs-library__comment");
            _shareButton = new Button(ToggleShare) { text = "Share" };
            _shareStatus = new Label();
            _shareStatus.AddToClassList("irs-panel__details");
            _openResultButton = new Button(() =>
            {
                if (!string.IsNullOrEmpty(_resultUrl)) Application.OpenURL(_resultUrl);
            }) { text = "Open" };
            _shareSection.Add(_shareTarget);
            _shareSection.Add(_shareComment);
            _shareSection.Add(_shareButton);
            _shareSection.Add(_shareStatus);
            _shareSection.Add(_openResultButton);
            _detail.Add(_shareSection);

            _recorder.Library.Changed += OnLibraryChanged;
            CloseDetail();
            SetSelectionMode(false);
        }

        public VisualElement Root { get; }

        public void Dispose()
        {
            _recorder.Library.Changed -= OnLibraryChanged;
            _sharing?.Cancel();
            _preview.Dispose();
            ReleaseThumbnails();
        }

        /// <summary>Called when the tab becomes visible or hidden.</summary>
        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (visible)
            {
                SetListStatus(string.Empty);
                _recovery.Refresh(true);
                RefreshList();
                if (_selected != null && File.Exists(_selected.FilePath)) _preview.Play(_selected.FilePath);
            }
            else
            {
                _preview.Stop();
            }
        }

        private void OnLibraryChanged()
        {
            if (!_visible) return;
            if (_selected != null && !File.Exists(_selected.FilePath)) CloseDetail();
            RefreshList();
        }

        private void RefreshList()
        {
            _list.Clear();
            ReleaseThumbnails();
            IReadOnlyList<RecordingEntry> entries;
            try
            {
                entries = _recorder.Library.GetEntries();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                entries = Array.Empty<RecordingEntry>();
            }

            _entries = entries;
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries) existing.Add(entry.FilePath);
            _checkedPaths.IntersectWith(existing);

            foreach (var entry in entries) _list.Add(CreateRow(entry));
            _emptyLabel.style.display = entries.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _selectButton.SetEnabled(entries.Count > 0 || _selectionMode);
            RefreshSelection();
        }

        private void SetListStatus(string message)
        {
            _listStatus.text = message;
            _listStatus.EnableInClassList("irs-panel__error--hidden", string.IsNullOrEmpty(message));
        }

        private void SetSelectionMode(bool enabled)
        {
            _selectionMode = enabled;
            _checkedPaths.Clear();
            _deleteCheckedButton.Disarm();
            _selectButton.text = enabled ? "Done" : "Select";
            _selectionBar.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;
            SetListStatus(string.Empty);
            if (_visible) RefreshList();
            else RefreshSelection();
        }

        private void SetAllChecked(bool value)
        {
            _checkedPaths.Clear();
            if (value)
                foreach (var entry in _entries)
                    _checkedPaths.Add(entry.FilePath);
            RefreshList();
        }

        private void ToggleChecked(RecordingEntry entry)
        {
            if (!_checkedPaths.Remove(entry.FilePath)) _checkedPaths.Add(entry.FilePath);
            RefreshList();
        }

        private void RefreshSelection()
        {
            var count = _checkedPaths.Count;
            _selectAllToggle.SetValueWithoutNotify(count > 0 && count == _entries.Count);
            _deleteCheckedButton.Label = count > 0 ? $"Delete {count}" : "Delete";
            _deleteCheckedButton.SetEnabled(count > 0);
            if (count == 0) _deleteCheckedButton.Disarm();
        }

        private void DeleteChecked()
        {
            var targets = new List<RecordingEntry>();
            foreach (var entry in _entries)
                if (_checkedPaths.Contains(entry.FilePath))
                    targets.Add(entry);
            if (targets.Count == 0) return;

            SetListStatus(string.Empty);
            try
            {
                _recorder.Library.Delete(targets);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                SetListStatus(ex.Message);
            }

            _checkedPaths.Clear();
            RefreshList();
        }

        private VisualElement CreateRow(RecordingEntry entry)
        {
            var row = new VisualElement();
            row.AddToClassList("irs-library__row");

            var isChecked = _selectionMode && _checkedPaths.Contains(entry.FilePath);
            row.EnableInClassList("irs-library__row--checked", isChecked);
            if (_selectionMode)
            {
                // The row handles the click, so the check box only displays the state.
                var check = new Toggle { value = isChecked, pickingMode = PickingMode.Ignore };
                check.AddToClassList("irs-library__check");
                foreach (var child in check.Query<VisualElement>().ToList()) child.pickingMode = PickingMode.Ignore;
                row.Add(check);
            }

            var thumbnail = Thumbnails.Load(entry.ThumbnailPath);
            if (thumbnail != null)
            {
                _thumbnails.Add(thumbnail);
                var image = new Image { image = thumbnail, scaleMode = ScaleMode.ScaleAndCrop };
                image.AddToClassList("irs-thumbnail");
                row.Add(image);
            }

            var text = new VisualElement();
            text.AddToClassList("irs-library__row-text");
            row.Add(text);

            var title = new Label($"{entry.Source} · {Formatting.LocalDateTime(entry.CreatedAtUtc)}");
            title.AddToClassList("irs-library__row-title");
            text.Add(title);

            var info = new Label(DescribeShort(entry));
            info.AddToClassList("irs-panel__details");
            text.Add(info);

            row.RegisterCallback<ClickEvent>(_ =>
            {
                if (_selectionMode) ToggleChecked(entry);
                else OpenDetail(entry);
            });
            return row;
        }

        private void OpenDetail(RecordingEntry entry)
        {
            _selected = entry;
            _resultUrl = null;
            _shareMessage = null;
            _shareComment.SetValueWithoutNotify(string.Empty);
            _deleteButton.Disarm();
            _listSection.style.display = DisplayStyle.None;
            _detail.style.display = DisplayStyle.Flex;
            _detailInfo.text = DescribeLong(entry);

            var targets = new List<string>();
            foreach (var target in _recorder.ShareTargets)
                if (target != null)
                    targets.Add(target.DisplayName);
            _shareTarget.choices = targets;
            if (targets.Count > 0 && _shareTarget.index < 0) _shareTarget.index = 0;
            _shareSection.style.display = targets.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshShare();

            if (_visible) _preview.Play(entry.FilePath);
        }

        private void CloseDetail()
        {
            _sharing?.Cancel();
            _preview.Stop();
            _selected = null;
            _detail.style.display = DisplayStyle.None;
            _listSection.style.display = DisplayStyle.Flex;
        }

        private void DeleteSelected()
        {
            var entry = _selected;
            if (entry == null || _sharing != null) return;
            // The player keeps the file open on some platforms, which would prevent its deletion.
            _preview.Stop();
            try
            {
                _recorder.Library.Delete(entry);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _detailInfo.text = DescribeLong(entry) + $"\nDeletion failed: {ex.Message}";
                return;
            }

            CloseDetail();
            RefreshList();
        }

        private void SetThumbnailFromPreview()
        {
            var entry = _selected;
            if (entry == null) return;
            try
            {
                var png = _preview.CaptureFramePng(320);
                if (png == null) return;
                _selected = _recorder.Library.SetThumbnail(entry, png);
                _detailInfo.text = DescribeLong(_selected) + "\nThumbnail updated.";
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _detailInfo.text = DescribeLong(entry) + $"\nThumbnail failed: {ex.Message}";
            }
        }

        private void ReleaseThumbnails()
        {
            foreach (var texture in _thumbnails)
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
            _thumbnails.Clear();
        }

        private ShareTarget GetSelectedTarget()
        {
            var index = _shareTarget.index;
            var count = 0;
            foreach (var target in _recorder.ShareTargets)
            {
                if (target == null) continue;
                if (count++ == index) return target;
            }

            return null;
        }

        private ShareRequest CreateRequest()
        {
            return new ShareRequest(new[] { _selected }) { Comment = _shareComment.value };
        }

        private void RefreshShare()
        {
            _openResultButton.style.display = string.IsNullOrEmpty(_resultUrl) ? DisplayStyle.None : DisplayStyle.Flex;
            if (_sharing != null)
            {
                _shareButton.text = "Cancel";
                _shareButton.SetEnabled(true);
                _deleteButton.SetEnabled(false);
                _thumbnailButton.SetEnabled(false);
                _shareTarget.SetEnabled(false);
                return;
            }

            _shareButton.text = "Share";
            _deleteButton.SetEnabled(true);
            _thumbnailButton.SetEnabled(true);
            _shareTarget.SetEnabled(true);

            var target = GetSelectedTarget();
            if (target == null || _selected == null)
            {
                _shareButton.SetEnabled(false);
                _shareStatus.text = string.Empty;
                return;
            }

            var canShare = target.CanShare(CreateRequest(), out var reason);
            _shareButton.SetEnabled(canShare);
            _shareStatus.text = canShare
                ? _shareMessage ?? string.Empty
                : reason ?? "This destination cannot accept the recording.";
        }

        private async void ToggleShare()
        {
            if (_sharing != null)
            {
                _sharing.Cancel();
                return;
            }

            var target = GetSelectedTarget();
            var entry = _selected;
            if (target == null || entry == null) return;

            using var cancellation = new CancellationTokenSource();
            _sharing = cancellation;
            _resultUrl = null;
            _shareMessage = null;
            _shareStatus.text = $"Sharing to {target.DisplayName}…";
            RefreshShare();

            var context = new ShareContext(new Progress<float>(p =>
            {
                if (_sharing == cancellation)
                    _shareStatus.text = string.Format(CultureInfo.InvariantCulture, "Sharing to {0}… {1:0}%",
                        target.DisplayName, p * 100);
            }));

            string message;
            string url = null;
            try
            {
                var result = await target.ShareAsync(CreateRequest(), context, cancellation.Token);
                url = result.Url;
                message = result.Message ?? $"Shared to {target.DisplayName}.";
            }
            catch (OperationCanceledException)
            {
                message = "Sharing cancelled.";
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                message = $"Sharing failed: {ex.Message}";
            }

            _sharing = null;
            // The user may have moved to another recording meanwhile.
            if (_selected == entry)
            {
                _resultUrl = url;
                _shareMessage = message;
            }

            RefreshShare();
        }

        private static string DescribeShort(RecordingEntry entry)
        {
            return entry.DurationSeconds > 0
                ? $"{Formatting.Time(entry.DurationSeconds)} · {Formatting.Bytes(entry.SizeBytes)}"
                : Formatting.Bytes(entry.SizeBytes);
        }

        private static string DescribeLong(RecordingEntry entry)
        {
            var builder = new StringBuilder();
            builder.Append(entry.Source).Append(" · ").AppendLine(Formatting.LocalDateTime(entry.CreatedAtUtc));
            builder.Append(DescribeShort(entry));
            foreach (var pair in entry.Metadata) builder.Append('\n').Append(pair.Key).Append(": ").Append(pair.Value);
            return builder.ToString();
        }
    }
}
