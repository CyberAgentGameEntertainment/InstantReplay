// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Modal view that previews a <see cref="ReplayDraft" />, lets the user choose the range and the thumbnail, and
    ///     saves the range to the library. The view owns the draft and disposes it when closed.
    /// </summary>
    internal sealed class TrimView : IDisposable
    {
        private const long TickIntervalMs = 50;
        private const double MinimumLengthSeconds = 0.1;
        private const int ThumbnailLongSide = 320;
        private const long LeaveTransitionMs = 180;

        private readonly Button _cancelButton;
        private readonly ReplayDraft _draft;
        private readonly Label _playheadLabel;
        private readonly Button _playButton;
        private readonly PreviewPlayer _preview;
        private readonly Label _rangeLabel;
        private readonly TrimTimeline _timeline;
        private readonly Button _saveButton;
        private readonly Button _setEndButton;
        private readonly Button _setStartButton;
        private readonly Label _statusLabel;
        private readonly Image _thumbnailImage;
        private readonly Button _thumbnailButton;
        private readonly IVisualElementScheduledItem _tick;

        private bool _closed;
        private double _end;
        private bool _resumeAfterScrub;
        private bool _saving;
        private double _start;
        private byte[] _thumbnailPng;
        private Texture2D _thumbnailTexture;

        public TrimView(VisualElement parent, ReplayDraft draft, Transform previewParent)
        {
            _draft = draft;
            _start = 0;
            _end = draft.DurationSeconds;

            Root = new VisualElement();
            Root.AddToClassList("irs-modal");
            var panel = new VisualElement();
            panel.AddToClassList("irs-modal__content");
            panel.AddToClassList("irs-panel");
            Root.Add(panel);

            var title = new Label("Trim replay");
            title.AddToClassList("irs-panel__title");
            panel.Add(title);

            _preview = new PreviewPlayer(previewParent);
            _preview.Image.AddToClassList("irs-trim__preview");
            _preview.Image.RegisterCallback<ClickEvent>(_ => TogglePlay());
            _preview.Error += message => _statusLabel.text = $"Preview failed: {message}";
            if (PreviewPlayer.IsSupported) panel.Add(_preview.Image);

            // On a short screen the preview shrinks first, and the controls scroll once it has reached its minimum.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("irs-trim__controls");
            panel.Add(scroll);
            var content = scroll.contentContainer;

            _timeline = new TrimTimeline();
            _timeline.SetDuration(draft.DurationSeconds, draft.KeyFrameTimestamps);
            _timeline.SetRange(_start, _end);
            _timeline.StartDragged += time => SetRange(time, _end, true);
            _timeline.EndDragged += time => SetRange(_start, time, false);
            _timeline.PlayheadDragged += Scrub;
            _timeline.PlayheadReleased += EndScrub;
            content.Add(_timeline);

            var labels = new VisualElement();
            labels.AddToClassList("irs-panel__row");
            labels.AddToClassList("irs-trim__labels");
            _rangeLabel = new Label();
            _rangeLabel.AddToClassList("irs-panel__details");
            _playheadLabel = new Label();
            _playheadLabel.AddToClassList("irs-panel__details");
            _playheadLabel.AddToClassList("irs-trim__playhead");
            labels.Add(_rangeLabel);
            labels.Add(_playheadLabel);
            content.Add(labels);

            var playback = new VisualElement();
            playback.AddToClassList("irs-panel__row");
            playback.AddToClassList("irs-panel__section");
            _setStartButton = new Button(() => SetRange(_preview.Time, _end, true)) { text = "Set start" };
            _playButton = new Button(TogglePlay) { text = "Pause" };
            _setEndButton = new Button(() => SetRange(_start, _preview.Time, false)) { text = "Set end" };
            playback.Add(_setStartButton);
            playback.Add(_playButton);
            playback.Add(_setEndButton);
            if (PreviewPlayer.IsSupported) content.Add(playback);

            var thumbnail = new VisualElement();
            thumbnail.AddToClassList("irs-panel__row");
            thumbnail.AddToClassList("irs-panel__section");
            thumbnail.AddToClassList("irs-trim__thumbnail-row");
            _thumbnailImage = new Image { scaleMode = ScaleMode.ScaleToFit };
            _thumbnailImage.AddToClassList("irs-thumbnail");
            _thumbnailImage.style.display = DisplayStyle.None;
            _thumbnailButton = new Button(CaptureThumbnail) { text = "Use frame as thumbnail" };
            thumbnail.Add(_thumbnailImage);
            thumbnail.Add(_thumbnailButton);
            if (PreviewPlayer.IsSupported && PreviewPlayer.CanCaptureFrame) content.Add(thumbnail);

            _statusLabel = new Label();
            _statusLabel.AddToClassList("irs-panel__details");
            content.Add(_statusLabel);

            var actions = new VisualElement();
            actions.AddToClassList("irs-panel__row");
            actions.AddToClassList("irs-panel__section");
            _cancelButton = new Button(() => Close(null)) { text = "Discard" };
            _saveButton = new Button(Save) { text = "Save" }.WithClass(Controls.Primary);
            actions.Add(_cancelButton);
            actions.Add(_saveButton);
            content.Add(actions);

            // The view is laid out in its initial state first, so that the transition to the final state runs.
            Root.AddToClassList("irs-modal--entering");
            parent.Add(Root);
            Root.schedule.Execute(() => Root.RemoveFromClassList("irs-modal--entering")).StartingIn(16);
            _tick = Root.schedule.Execute(Tick).Every(TickIntervalMs);
            RefreshLabels();
            StartPreview();
        }

        public VisualElement Root { get; }

        /// <summary>Raised once when the view closes, with the saved recording or null when nothing was saved.</summary>
        public event Action<RecordingEntry> Closed;

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _tick.Pause();
            _preview.Dispose();
            _draft.Dispose();
            if (_thumbnailTexture != null) UnityEngine.Object.Destroy(_thumbnailTexture);

            // The view fades out before it is removed. Its controls no longer respond meanwhile.
            Root.pickingMode = PickingMode.Ignore;
            Root.SetEnabled(false);
            Root.AddToClassList("irs-modal--leaving");
            Root.schedule.Execute(Root.RemoveFromHierarchy).StartingIn(LeaveTransitionMs);
        }

        private async void StartPreview()
        {
            if (!PreviewPlayer.IsSupported) return;
            _statusLabel.text = "Preparing preview…";
            try
            {
                var path = await _draft.GetPreviewPathAsync();
                if (_closed) return;
                _statusLabel.text = string.Empty;
                _preview.Play(path);
            }
            catch (Exception ex)
            {
                if (_closed) return;
                Debug.LogException(ex);
                _statusLabel.text = $"Preview failed: {ex.Message}";
            }
        }

        private void Tick()
        {
            if (!_preview.HasFrame) return;
            var time = _preview.Time;
            // Playback loops within the selected range.
            if (_preview.IsPlaying && (time >= _end || time < _start - 0.25)) _preview.Seek(_start);
            if (_timeline.IsDraggingPlayhead) return;
            _timeline.SetPlayhead(time);
            _playheadLabel.text = Formatting.PreciseTime(time);
            _playButton.text = _preview.IsPlaying ? "Pause" : "Play";
        }

        // Playback pauses while the knob is dragged, so that the frame under the knob stays on screen.
        private void Scrub(double time)
        {
            if (_preview.IsPlaying)
            {
                _resumeAfterScrub = true;
                _preview.Pause();
            }

            time = Math.Max(_start, Math.Min(_end, time));
            _timeline.SetPlayhead(time);
            _playheadLabel.text = Formatting.PreciseTime(time);
            _preview.Seek(time);
        }

        private void EndScrub()
        {
            if (!_resumeAfterScrub) return;
            _resumeAfterScrub = false;
            _preview.Resume();
        }

        // Snaps the start to the key frame where the saved recording will actually start, and keeps the end after it.
        private void SetRange(double start, double end, bool seekToStart)
        {
            var duration = _draft.DurationSeconds;
            _start = _draft.GetSaveStart(Math.Max(0, Math.Min(start, duration)));
            _end = Math.Min(duration, Math.Max(end, _start + MinimumLengthSeconds));
            _timeline.SetRange(_start, _end);
            RefreshLabels();
            var position = seekToStart ? _start : Math.Max(_start, _end - 1);
            _timeline.SetPlayhead(position);
            _preview.Seek(position);
        }

        private void RefreshLabels()
        {
            _rangeLabel.text =
                $"{Formatting.PreciseTime(_start)} – {Formatting.PreciseTime(_end)} ({Formatting.PreciseTime(_end - _start)})";
        }

        private void TogglePlay()
        {
            if (_preview.IsPlaying) _preview.Pause();
            else _preview.Resume();
        }

        private void CaptureThumbnail()
        {
            byte[] png;
            try
            {
                png = _preview.CaptureFramePng(ThumbnailLongSide);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _statusLabel.text = $"Thumbnail failed: {ex.Message}";
                return;
            }

            if (png == null) return;
            _thumbnailPng = png;
            if (_thumbnailTexture != null) UnityEngine.Object.Destroy(_thumbnailTexture);
            _thumbnailTexture = Thumbnails.Decode(png);
            _thumbnailImage.image = _thumbnailTexture;
            _thumbnailImage.style.display = _thumbnailTexture != null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private async void Save()
        {
            if (_saving || _closed) return;
            _saving = true;
            SetControlsEnabled(false);
            _preview.Pause();
            _statusLabel.text = "Saving…";

            try
            {
                // The end is left open when it reaches the last frame, so that the last frame is included.
                double? end = _end >= _draft.DurationSeconds ? null : _end;
                var entry = await _draft.SaveAsync(_start, end, _thumbnailPng);
                Close(entry);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                if (_closed) return;
                _statusLabel.text = $"Saving failed: {ex.Message}";
                _saving = false;
                SetControlsEnabled(true);
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            _timeline.SetEnabled(enabled);
            _setStartButton.SetEnabled(enabled);
            _setEndButton.SetEnabled(enabled);
            _thumbnailButton.SetEnabled(enabled);
            _saveButton.SetEnabled(enabled);
            _cancelButton.SetEnabled(enabled);
        }

        private void Close(RecordingEntry saved)
        {
            if (_closed) return;
            Dispose();
            Closed?.Invoke(saved);
        }
    }
}
