// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Modal view that lets the user choose recordings from the library and footage left behind by abnormal
    ///     terminations. Chosen footage of the latter kind is recovered into the library before the view closes.
    /// </summary>
    internal sealed class RecordingPickerView : IDisposable
    {
        private const long LeaveTransitionMs = 180;

        private readonly bool _allowMultiple;
        private readonly Button _cancelButton;
        private readonly HashSet<object> _checked = new();
        private readonly Button _confirmButton;
        private readonly Label _emptyLabel;
        private readonly VisualElement _list;
        private readonly InstantReplayRecorder _recorder;
        private readonly Label _statusLabel;
        private readonly List<Texture2D> _thumbnails = new();

        private bool _busy;
        private bool _closed;
        private IReadOnlyList<RecordingEntry> _entries = Array.Empty<RecordingEntry>();

        private IReadOnlyList<DiskEncodedFrameBufferRecovery> _recoveries =
            Array.Empty<DiskEncodedFrameBufferRecovery>();

        public RecordingPickerView(VisualElement parent, InstantReplayRecorder recorder, bool allowMultiple)
        {
            _recorder = recorder;
            _allowMultiple = allowMultiple;

            Root = new VisualElement();
            Root.AddToClassList("irs-modal");
            var panel = new VisualElement();
            panel.AddToClassList("irs-modal__content");
            panel.AddToClassList("irs-panel");
            panel.AddToClassList("irs-picker");
            Root.Add(panel);

            var title = new Label(allowMultiple ? Strings.Current.ChooseRecordings : Strings.Current.ChooseRecording);
            title.AddToClassList("irs-panel__title");
            panel.Add(title);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("irs-picker__list");
            panel.Add(scroll);
            _list = scroll.contentContainer;

            _emptyLabel = new Label(Strings.Current.NoRecordings);
            _emptyLabel.AddToClassList("irs-panel__details");
            panel.Add(_emptyLabel);

            _statusLabel = new Label();
            _statusLabel.AddToClassList("irs-panel__details");
            panel.Add(_statusLabel);

            var actions = new VisualElement();
            actions.AddToClassList("irs-panel__row");
            actions.AddToClassList("irs-panel__section");
            actions.AddToClassList("irs-picker__actions");
            _cancelButton = new Button(Cancel) { text = Strings.Current.Cancel };
            _confirmButton = new Button(Confirm) { text = Strings.Current.Use }.WithClass(Controls.Primary);
            actions.Add(_cancelButton);
            actions.Add(_confirmButton);
            panel.Add(actions);

            Taps.Apply(Root);
            Root.AddToClassList("irs-modal--entering");
            parent.Add(Root);
            Root.schedule.Execute(() => Root.RemoveFromClassList("irs-modal--entering")).StartingIn(16);

            Reload();
        }

        public VisualElement Root { get; }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            ReleaseThumbnails();

            Root.pickingMode = PickingMode.Ignore;
            Root.SetEnabled(false);
            Root.AddToClassList("irs-modal--leaving");
            Root.schedule.Execute(Root.RemoveFromHierarchy).StartingIn(LeaveTransitionMs);
        }

        /// <summary>Raised once when the view closes, with the chosen recordings. The list is empty when cancelled.</summary>
        public event Action<IReadOnlyList<RecordingEntry>> Closed;

        /// <summary>Closes the view as if the user had cancelled.</summary>
        public void Cancel()
        {
            Close(Array.Empty<RecordingEntry>());
        }

        private void Reload()
        {
            try
            {
                _entries = _recorder.Library.GetEntries();
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
                _entries = Array.Empty<RecordingEntry>();
            }

            try
            {
                _recoveries = DiskEncodedFrameBufferRecovery.FindRecoverable();
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
                _recoveries = Array.Empty<DiskEncodedFrameBufferRecovery>();
            }

            Rebuild();
        }

        private void Rebuild()
        {
            _list.Clear();
            ReleaseThumbnails();

            foreach (var recovery in _recoveries)
            {
                var details = Formatting.Bytes(recovery.SizeBytes);
                if (!recovery.IsCompatible)
                    details += "\n" + Strings.Format(Strings.Current.RecordedBy, recovery.Platform,
                        recovery.ApplicationVersion);
                var row = RecordingRows.Create(
                    Strings.Format(Strings.Current.Unfinished, Formatting.LocalDateTime(recovery.StartedAtUtc)),
                    details, null, _allowMultiple, _checked.Contains(recovery));
                row.AddToClassList("irs-picker__row--unfinished");
                row.OnTap(() => Toggle(recovery));
                _list.Add(row);
            }

            foreach (var entry in _entries)
            {
                var thumbnail = Thumbnails.Load(entry.ThumbnailPath);
                if (thumbnail != null) _thumbnails.Add(thumbnail);
                var row = RecordingRows.Create(RecordingRows.Title(entry), RecordingRows.DescribeShort(entry),
                    thumbnail, _allowMultiple, _checked.Contains(entry));
                row.OnTap(() => Toggle(entry));
                _list.Add(row);
            }

            _emptyLabel.style.display =
                _entries.Count == 0 && _recoveries.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _confirmButton.text = _allowMultiple && _checked.Count > 1
                ? Strings.Format(Strings.Current.UseCount, _checked.Count)
                : Strings.Current.Use;
            _confirmButton.SetEnabled(!_busy && _checked.Count > 0);
            _cancelButton.SetEnabled(!_busy);
            _list.SetEnabled(!_busy);
        }

        private void Toggle(object item)
        {
            if (_busy) return;
            if (!_checked.Remove(item))
            {
                if (!_allowMultiple) _checked.Clear();
                _checked.Add(item);
            }

            Rebuild();
        }

        private async void Confirm()
        {
            if (_busy || _closed || _checked.Count == 0) return;
            _busy = true;
            _statusLabel.text = string.Empty;
            Rebuild();

            var chosen = new List<RecordingEntry>();
            try
            {
                foreach (var recovery in _recoveries)
                {
                    if (!_checked.Contains(recovery)) continue;
                    _statusLabel.text = Strings.Current.Recovering;
                    var entry = await _recorder.Library.ImportRecoveredAsync(recovery);
                    // The footage now exists in the library, so the raw buffer is no longer the only copy.
                    recovery.Delete();
                    chosen.Add(entry);
                    if (_closed) return;
                }

                foreach (var entry in _entries)
                    if (_checked.Contains(entry))
                        chosen.Add(entry);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
                if (_closed) return;
                _busy = false;
                _checked.Clear();
                Reload();
                _statusLabel.text = Strings.Format(Strings.Current.RecoveryFailed, ex.Message);
                return;
            }

            Close(chosen);
        }

        private void ReleaseThumbnails()
        {
            foreach (var texture in _thumbnails)
                if (texture != null)
                    Object.Destroy(texture);
            _thumbnails.Clear();
        }

        private void Close(IReadOnlyList<RecordingEntry> chosen)
        {
            if (_closed) return;
            Dispose();
            Closed?.Invoke(chosen);
        }
    }
}
