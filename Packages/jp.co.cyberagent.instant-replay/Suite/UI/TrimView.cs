// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Modal view that previews a <see cref="ReplayDraft" /> and lets the user choose the range and the thumbnail.
    ///     When it saves to the library, the view owns the draft and disposes it when closed. Otherwise it only returns
    ///     the selection, and the draft stays with the caller.
    /// </summary>
    internal sealed class TrimView : IDisposable
    {
        private const long TickIntervalMs = 50;
        private const double MinimumLengthSeconds = 0.1;
        private const int ThumbnailLongSide = 320;
        private const long LeaveTransitionMs = 180;

        private readonly Button _cancelButton;
        private readonly ReplayDraft _draft;
        private readonly Button _playButton;
        private readonly Label _playheadLabel;
        private readonly PreviewPlayer _preview;
        private readonly Label _rangeLabel;
        private readonly Button _saveButton;
        private readonly bool _saveToLibrary;
        private readonly Button _setEndButton;
        private readonly Button _setStartButton;
        private readonly Label _statusLabel;
        private readonly Button _thumbnailButton;
        private readonly Image _thumbnailImage;
        private readonly IVisualElementScheduledItem _tick;
        private readonly TrimTimeline _timeline;

        private bool _closed;
        private double _end;
        private bool _resumeAfterScrub;
        private bool _saving;
        private double _start;
        private byte[] _thumbnailPng;
        private Texture2D _thumbnailTexture;

        public TrimView(VisualElement parent, ReplayDraft draft, Transform previewParent, bool saveToLibrary)
        {
            _draft = draft;
            _saveToLibrary = saveToLibrary;
            _start = 0;
            _end = draft.DurationSeconds;

            Root = new VisualElement();
            Root.AddToClassList("irs-modal");
            var panel = new VisualElement();
            panel.AddToClassList("irs-modal__content");
            panel.AddToClassList("irs-panel");
            Root.Add(panel);

            var title = new Label(Strings.Current.TrimReplay);
            title.AddToClassList("irs-panel__title");
            panel.Add(title);

            _preview = new PreviewPlayer(previewParent);
            _preview.Image.AddToClassList("irs-trim__preview");
            _preview.Image.OnTap(TogglePlay);
            _preview.Error += message => _statusLabel.text = Strings.Format(Strings.Current.PreviewFailed, message);
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
            _setStartButton = new Button(() => SetRange(_preview.Time, _end, true)) { text = Strings.Current.SetStart };
            _playButton = new Button(TogglePlay) { text = Strings.Current.Pause };
            _setEndButton = new Button(() => SetRange(_start, _preview.Time, false)) { text = Strings.Current.SetEnd };
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
            _thumbnailButton = new Button(CaptureThumbnail) { text = Strings.Current.UseFrameAsThumbnail };
            thumbnail.Add(_thumbnailImage);
            thumbnail.Add(_thumbnailButton);
            if (PreviewPlayer.IsSupported && PreviewPlayer.CanCaptureFrame) content.Add(thumbnail);

            _statusLabel = new Label();
            _statusLabel.AddToClassList("irs-panel__details");
            content.Add(_statusLabel);

            var actions = new VisualElement();
            actions.AddToClassList("irs-panel__row");
            actions.AddToClassList("irs-panel__section");
            _cancelButton = new Button(Cancel)
                { text = saveToLibrary ? Strings.Current.Discard : Strings.Current.Cancel };
            _saveButton =
                new Button(Confirm) { text = saveToLibrary ? Strings.Current.Save : Strings.Current.Use }.WithClass(
                    Controls.Primary);
            actions.Add(_cancelButton);
            actions.Add(_saveButton);
            content.Add(actions);

            Taps.Apply(Root);

            // The view is laid out in its initial state first, so that the transition to the final state runs.
            Root.AddToClassList("irs-modal--entering");
            parent.Add(Root);
            Root.schedule.Execute(() => Root.RemoveFromClassList("irs-modal--entering")).StartingIn(16);
            _tick = Root.schedule.Execute(Tick).Every(TickIntervalMs);
            RefreshLabels();
            StartPreview();
        }

        public VisualElement Root { get; }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _tick.Pause();
            _preview.Dispose();
            if (_saveToLibrary) _draft.Dispose();
            if (_thumbnailTexture != null) Object.Destroy(_thumbnailTexture);

            // The view fades out before it is removed. Its controls no longer respond meanwhile.
            Root.pickingMode = PickingMode.Ignore;
            Root.SetEnabled(false);
            Root.AddToClassList("irs-modal--leaving");
            Root.schedule.Execute(Root.RemoveFromHierarchy).StartingIn(LeaveTransitionMs);
        }

        /// <summary>
        ///     Raised once when the view closes, with the selection and, when the view saves to the library, the saved
        ///     recording. Both are null when the user cancelled.
        /// </summary>
        public event Action<ReplaySelection, RecordingEntry> Closed;

        /// <summary>Closes the view as if the user had cancelled.</summary>
        public void Cancel()
        {
            Close(null, null);
        }

        private async void StartPreview()
        {
            if (!PreviewPlayer.IsSupported) return;
            _statusLabel.text = Strings.Current.PreparingPreview;
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
                ILogger.LogExceptionCore(ex);
                _statusLabel.text = Strings.Format(Strings.Current.PreviewFailed, ex.Message);
            }
        }

        private void Tick()
        {
            if (!_preview.HasFrame) return;
            var time = _preview.Time;
            // Playback loops within the selected range.
            if (_preview.IsPlaying && (time >= _end || time < _start - 0.25)) _preview.Seek(_start);
            if (_timeline.IsDragging) return;
            _timeline.SetPlayhead(time);
            _playheadLabel.text = Formatting.PreciseTime(time);
            _playButton.text = _preview.IsPlaying ? Strings.Current.Pause : Strings.Current.Play;
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
                ILogger.LogExceptionCore(ex);
                _statusLabel.text = Strings.Format(Strings.Current.ThumbnailFailed, ex.Message);
                return;
            }

            if (png == null) return;
            _thumbnailPng = png;
            if (_thumbnailTexture != null) Object.Destroy(_thumbnailTexture);
            _thumbnailTexture = Thumbnails.Decode(png);
            _thumbnailImage.image = _thumbnailTexture;
            _thumbnailImage.style.display = _thumbnailTexture != null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private ReplaySelection CreateSelection()
        {
            // The end is left open when it reaches the last frame, so that the last frame is included.
            double? end = _end >= _draft.DurationSeconds ? null : _end;
            return new ReplaySelection(_start, end, _thumbnailPng);
        }

        private void Confirm()
        {
            if (_saving || _closed) return;
            if (_saveToLibrary) Save();
            else Close(CreateSelection(), null);
        }

        private async void Save()
        {
            _saving = true;
            SetControlsEnabled(false);
            _preview.Pause();
            _statusLabel.text = Strings.Current.Saving;

            try
            {
                var selection = CreateSelection();
                var entry = await _draft.SaveAsync(selection);
                Close(selection, entry);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
                if (_closed) return;
                _statusLabel.text = Strings.Format(Strings.Current.SavingFailed, ex.Message);
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

        private void Close(ReplaySelection selection, RecordingEntry saved)
        {
            if (_closed) return;
            Dispose();
            Closed?.Invoke(selection, saved);
        }
    }
}
