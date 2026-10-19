// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Lists disk buffers left behind by abnormal terminations, and recovers them into the library or deletes them.
    /// </summary>
    internal sealed class RecoveryView
    {
        private readonly VisualElement _list;
        private readonly InstantReplayRecorder _recorder;
        private readonly Label _status;
        private bool _busy;

        public RecoveryView(InstantReplayRecorder recorder)
        {
            _recorder = recorder;

            Root = new VisualElement();
            Root.AddToClassList("irs-recovery");

            var title = new Label("Unfinished recordings");
            title.AddToClassList("irs-recovery__title");
            Root.Add(title);

            var description = new Label("Footage buffered on disk before the application ended unexpectedly.");
            description.AddToClassList("irs-panel__details");
            Root.Add(description);

            _list = new VisualElement();
            Root.Add(_list);

            _status = new Label();
            _status.AddToClassList("irs-panel__details");
            Root.Add(_status);
        }

        public VisualElement Root { get; }

        /// <summary>Searches the disk buffer directory again and rebuilds the list.</summary>
        /// <param name="clearStatus">Whether to clear the outcome of the previous operation.</param>
        public void Refresh(bool clearStatus = false)
        {
            if (_busy) return;
            if (clearStatus) _status.text = string.Empty;

            IReadOnlyList<DiskEncodedFrameBufferRecovery> recoveries;
            try
            {
                recoveries = DiskEncodedFrameBufferRecovery.FindRecoverable();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                recoveries = Array.Empty<DiskEncodedFrameBufferRecovery>();
            }

            _list.Clear();
            foreach (var recovery in recoveries) _list.Add(CreateRow(recovery));
            Root.style.display = recoveries.Count > 0 || !string.IsNullOrEmpty(_status.text)
                ? DisplayStyle.Flex
                : DisplayStyle.None;
        }

        private VisualElement CreateRow(DiskEncodedFrameBufferRecovery recovery)
        {
            var row = new VisualElement();
            row.AddToClassList("irs-recovery__row");

            var text = $"{Formatting.LocalDateTime(recovery.StartedAtUtc)} · {Formatting.Bytes(recovery.SizeBytes)}";
            if (!recovery.IsCompatible) text += $"\nRecorded by {recovery.Platform} {recovery.ApplicationVersion}";
            var label = new Label(text);
            label.AddToClassList("irs-recovery__label");
            row.Add(label);

            var buttons = new VisualElement();
            buttons.AddToClassList("irs-panel__row");
            buttons.Add(new Button(() => Recover(recovery)) { text = "Recover" }.WithClass(Controls.Primary));
            buttons.Add(new ConfirmButton("Delete", () => Delete(recovery)));
            row.Add(buttons);
            return row;
        }

        private async void Recover(DiskEncodedFrameBufferRecovery recovery)
        {
            if (_busy) return;
            _busy = true;
            _list.SetEnabled(false);
            _status.text = "Recovering…";
            try
            {
                await _recorder.Library.ImportRecoveredAsync(recovery);
                // The footage now exists in the library, so the raw buffer is no longer the only copy.
                recovery.Delete();
                _status.text = "Recovered into the library.";
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _status.text = $"Recovery failed: {ex.Message}";
            }
            finally
            {
                _busy = false;
                _list.SetEnabled(true);
                Refresh();
            }
        }

        private void Delete(DiskEncodedFrameBufferRecovery recovery)
        {
            if (_busy) return;
            recovery.Delete();
            _status.text = string.Empty;
            Refresh();
        }
    }
}
