// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
// The Device variants report the simulated device in the Device Simulator, and the actual device elsewhere.
using Screen = UnityEngine.Device.Screen;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Overlay and panel built in code, so that no UXML or prefab has to stay loadable across Unity versions.
    /// </summary>
    internal sealed class SuiteView : IDisposable
    {
        private const long RefreshIntervalMs = 250;
        private const long PulseIntervalMs = 600;
        private const long PanelTransitionMs = 160;

        private readonly Toggle _bufferingToggle;
        private readonly Label _detailsLabel;
        private readonly Label _errorLabel;
        private readonly LibraryView _library;
        private readonly VisualElement _libraryTab;
        private readonly Button _libraryTabButton;
        private readonly VisualElement _overlay;
        private readonly VisualElement _overlayDot;
        private readonly Label _overlayLabel;
        private readonly VisualElement _panel;
        private readonly Button _pauseButton;
        private readonly DropdownField _presetDropdown;
        private readonly PresetEditorView _presetEditor;
        private readonly InstantReplayRecorder _recorder;
        private readonly VisualElement _recorderTab;
        private readonly Button _recorderTabButton;
        private readonly IVisualElementScheduledItem _pulse;
        private readonly IVisualElementScheduledItem _refresh;
        private readonly VisualElement _root;
        private readonly Transform _previewParent;
        private readonly Button _saveButton;
        private readonly Button _unboundedButton;

        private bool _busy;
        private bool _disposed;
        private bool _libraryTabSelected;
        private string _operationError;
        private RecordingPickerView _picker;
        private TrimView _trim;

        public SuiteView(VisualElement documentRoot, StyleSheet styleSheet, InstantReplayRecorder recorder,
            Transform previewParent)
        {
            _recorder = recorder;
            _previewParent = previewParent;

            _root = new VisualElement { pickingMode = PickingMode.Ignore };
            _root.AddToClassList("irs-root");
            if (styleSheet != null) _root.styleSheets.Add(styleSheet);
            documentRoot.Add(_root);

            _overlay = new VisualElement();
            _overlay.AddToClassList("irs-overlay");
            _overlayDot = new VisualElement();
            _overlayDot.AddToClassList("irs-overlay__dot");
            _overlayLabel = new Label();
            _overlayLabel.AddToClassList("irs-overlay__label");
            _overlay.Add(_overlayDot);
            _overlay.Add(_overlayLabel);
            _overlay.RegisterCallback<ClickEvent>(_ => TogglePanel());
            _root.Add(_overlay);

            _panel = new VisualElement();
            _panel.AddToClassList("irs-panel");
            _panel.AddToClassList("irs-panel--hidden");
            _root.Add(_panel);

            // The panel can be taller than a landscape phone screen once the preset editor is expanded.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("irs-panel__scroll");
            _panel.Add(scroll);
            var content = scroll.contentContainer;

            var header = new VisualElement();
            header.AddToClassList("irs-panel__header");
            var title = new Label("Instant Replay");
            title.AddToClassList("irs-panel__title");
            header.Add(title);
            var tabs = new VisualElement();
            tabs.AddToClassList("irs-tabs");
            _recorderTabButton = new Button(() => SelectTab(false)) { text = "Recorder" };
            _libraryTabButton = new Button(() => SelectTab(true)) { text = "Recordings" };
            _recorderTabButton.AddToClassList("irs-tab");
            _libraryTabButton.AddToClassList("irs-tab");
            tabs.Add(_recorderTabButton);
            tabs.Add(_libraryTabButton);
            header.Add(tabs);
            content.Add(header);

            _libraryTab = new VisualElement();
            _library = new LibraryView(recorder, previewParent);
            _libraryTab.Add(_library.Root);
            content.Add(_libraryTab);

            _recorderTab = new VisualElement();
            content.Add(_recorderTab);
            content = _recorderTab;

            _bufferingToggle = new Toggle("Continuous buffering").AsSwitch();
            _bufferingToggle.RegisterValueChangedCallback(e => Run(() => _recorder.SetBufferingEnabled(e.newValue)));
            content.Add(_bufferingToggle);

            var actions = new VisualElement();
            actions.AddToClassList("irs-panel__row");
            actions.AddToClassList("irs-panel__section");
            // Saving a replay always goes through the trim view, where the whole footage is selected initially.
            _saveButton = new Button(SaveReplay) { text = "Save replay" }.WithClass(Controls.Primary);
            _unboundedButton = new Button(ToggleUnbounded);
            actions.Add(_saveButton);
            actions.Add(_unboundedButton);
            content.Add(actions);

            _pauseButton = new Button(() => Run(() =>
            {
                if (_recorder.IsPaused) _recorder.Resume();
                else _recorder.Pause();
            }));
            content.Add(_pauseButton);

            var presetSection = new VisualElement();
            presetSection.AddToClassList("irs-panel__section");
            var presetNames = new List<string>();
            foreach (var preset in _recorder.Presets) presetNames.Add(preset.DisplayName);
            _presetDropdown = new DropdownField("Preset", presetNames, 0);
            _presetDropdown.RegisterValueChangedCallback(_ => Run(() =>
            {
                var index = _presetDropdown.index;
                if (index >= 0 && index < _recorder.Presets.Count &&
                    _recorder.Presets[index] != _recorder.ActivePreset)
                    _recorder.ApplyPreset(_recorder.Presets[index]);
            }));
            _detailsLabel = new Label();
            _detailsLabel.AddToClassList("irs-panel__details");
            presetSection.Add(_presetDropdown);
            presetSection.Add(_detailsLabel);
            _presetEditor = new PresetEditorView(_recorder, Run);
            presetSection.Add(_presetEditor.Root);
            content.Add(presetSection);

            _errorLabel = new Label();
            _errorLabel.AddToClassList("irs-panel__error");
            content.Add(_errorLabel);

            _recorder.StateChanged += OnRecorderChanged;
            _recorder.ConfigurationChanged += OnRecorderChanged;
            _root.RegisterCallback<GeometryChangedEvent>(_ => ApplySafeArea());

            _refresh = _root.schedule.Execute(Refresh).Every(RefreshIntervalMs);
            _pulse = _root.schedule.Execute(Pulse).Every(PulseIntervalMs);
            SelectTab(false);
            Refresh();
        }

        /// <summary>
        ///     Whether the overlay and the panel are shown. Views opened from code are shown regardless.
        /// </summary>
        public bool IsVisible
        {
            get => !_root.ClassListContains("irs-root--chrome-hidden");
            set
            {
                _root.EnableInClassList("irs-root--chrome-hidden", !value);
                _library.SetVisible(value && IsPanelOpen && _libraryTabSelected);
            }
        }

        private bool HasModal => _trim != null || _picker != null;

        public void Dispose()
        {
            _disposed = true;
            _refresh.Pause();
            _pulse.Pause();
            // Cancelling completes the tasks of views opened from code.
            _trim?.Cancel();
            _picker?.Cancel();
            _library.Dispose();
            _recorder.StateChanged -= OnRecorderChanged;
            _recorder.ConfigurationChanged -= OnRecorderChanged;
            _root.RemoveFromHierarchy();
        }

        private bool IsPanelOpen => !_panel.ClassListContains("irs-panel--hidden") &&
                                    !_panel.ClassListContains("irs-panel--entering");

        private void TogglePanel()
        {
            if (IsPanelOpen)
            {
                // The panel fades out before it is removed from the layout.
                _panel.AddToClassList("irs-panel--entering");
                _panel.schedule.Execute(() =>
                {
                    if (_panel.ClassListContains("irs-panel--entering")) _panel.AddToClassList("irs-panel--hidden");
                }).StartingIn(PanelTransitionMs);
            }
            else
            {
                // The panel is laid out in its initial state first, so that the transition to the final state runs.
                _panel.AddToClassList("irs-panel--entering");
                _panel.RemoveFromClassList("irs-panel--hidden");
                _panel.schedule.Execute(() => _panel.RemoveFromClassList("irs-panel--entering")).StartingIn(16);
            }

            _library.SetVisible(IsVisible && IsPanelOpen && _libraryTabSelected);
        }

        // The dot blinks while an unbounded recording runs.
        private void Pulse()
        {
            var blinking = _recorder.State == RecorderState.RecordingUnbounded && !_recorder.IsPaused;
            _overlayDot.EnableInClassList("irs-overlay__dot--dim",
                blinking && !_overlayDot.ClassListContains("irs-overlay__dot--dim"));
        }

        private void SelectTab(bool library)
        {
            _libraryTabSelected = library;
            _recorderTabButton.EnableInClassList("irs-tab--selected", !library);
            _libraryTabButton.EnableInClassList("irs-tab--selected", library);
            _recorderTab.style.display = library ? DisplayStyle.None : DisplayStyle.Flex;
            _libraryTab.style.display = library ? DisplayStyle.Flex : DisplayStyle.None;
            _library.SetVisible(IsVisible && IsPanelOpen && library);
        }

        private void OnRecorderChanged(RecorderState _)
        {
            Refresh();
        }

        private void OnRecorderChanged()
        {
            Refresh();
        }

        private async void SaveReplay()
        {
            if (_busy || HasModal) return;
            _busy = true;
            _operationError = null;
            Refresh();
            try
            {
                var draft = await _recorder.CaptureReplayAsync();
                _trim = new TrimView(_root, draft, _previewParent, true);
                _trim.Closed += (_, saved) =>
                {
                    _trim = null;
                    if (saved != null) SelectTab(true);
                    Refresh();
                };
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _operationError = ex.Message;
            }
            finally
            {
                _busy = false;
                Refresh();
            }
        }

        public Task<ReplaySelection> TrimAsync(ReplayDraft draft, CancellationToken cancellationToken)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (draft.IsDisposed) throw new ObjectDisposedException(nameof(ReplayDraft));
            ThrowIfModalOpen();
            cancellationToken.ThrowIfCancellationRequested();

            var completion = new TaskCompletionSource<ReplaySelection>();
            var trim = new TrimView(_root, draft, _previewParent, false);
            _trim = trim;
            // The callback runs on the main thread even when the token is cancelled on another thread.
            var registration = cancellationToken.Register(trim.Cancel, true);
            trim.Closed += (selection, _) =>
            {
                registration.Dispose();
                _trim = null;
                Refresh();
                if (selection == null && cancellationToken.IsCancellationRequested)
                    completion.TrySetCanceled(cancellationToken);
                else
                    completion.TrySetResult(selection);
            };
            Refresh();
            return completion.Task;
        }

        public Task<IReadOnlyList<RecordingEntry>> PickRecordingsAsync(bool allowMultiple,
            CancellationToken cancellationToken)
        {
            ThrowIfModalOpen();
            cancellationToken.ThrowIfCancellationRequested();

            var completion = new TaskCompletionSource<IReadOnlyList<RecordingEntry>>();
            var picker = new RecordingPickerView(_root, _recorder, allowMultiple);
            _picker = picker;
            var registration = cancellationToken.Register(picker.Cancel, true);
            picker.Closed += chosen =>
            {
                registration.Dispose();
                _picker = null;
                Refresh();
                if (chosen.Count == 0 && cancellationToken.IsCancellationRequested)
                    completion.TrySetCanceled(cancellationToken);
                else
                    completion.TrySetResult(chosen);
            };
            Refresh();
            return completion.Task;
        }

        private void ThrowIfModalOpen()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(InstantReplaySuite));
            if (HasModal) throw new InvalidOperationException("Another view of the suite is open.");
        }

        private async void ToggleUnbounded()
        {
            if (_busy) return;
            _operationError = null;
            var recording = _recorder.CurrentUnboundedRecording;
            if (recording == null)
            {
                Run(() => _recorder.StartUnbounded());
                return;
            }

            _busy = true;
            Refresh();
            try
            {
                await recording.StopAsync();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _operationError = ex.Message;
            }
            finally
            {
                _busy = false;
                Refresh();
            }
        }

        private void Run(Action action)
        {
            _operationError = null;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _operationError = ex.Message;
            }

            Refresh();
        }

        private void Refresh()
        {
            if (_disposed) return;
            var state = _recorder.State;
            RecorderStatistics stats;
            try
            {
                stats = _recorder.GetStatistics();
            }
            catch (Exception ex)
            {
                // The statistics are only for display; a failure here must not stop the overlay.
                Debug.LogException(ex);
                stats = default;
            }

            UpdateOverlay(state, stats);

            var idle = !_busy && state is not (RecorderState.Exporting or RecorderState.RecordingUnbounded);
            _bufferingToggle.SetValueWithoutNotify(_recorder.IsBufferingEnabled);
            _saveButton.SetEnabled(!_busy && !HasModal && state == RecorderState.Buffering);
            _unboundedButton.text = state == RecorderState.RecordingUnbounded ? "Stop recording" : "Start recording";
            _unboundedButton.EnableInClassList("irs-button--recording", state == RecorderState.RecordingUnbounded);
            _unboundedButton.SetEnabled(!_busy && state != RecorderState.Exporting);
            _pauseButton.text = _recorder.IsPaused ? "Resume" : "Pause";
            _presetDropdown.SetEnabled(idle);

            var activeIndex = IndexOf(_recorder.Presets, _recorder.ActivePreset);
            // A preset applied from code need not be one of the choices; its name is shown all the same.
            var activeName = activeIndex >= 0
                ? _presetDropdown.choices[activeIndex]
                : _recorder.ActivePreset.DisplayName;
            if (_presetDropdown.value != activeName) _presetDropdown.SetValueWithoutNotify(activeName);
            _detailsLabel.text = DescribePreset(_recorder.ActivePreset, stats);
            _presetEditor.Refresh(idle);

            var error = _operationError ??
                        (state == RecorderState.Faulted ? _recorder.LastException?.Message ?? "Recording failed." : null);
            _errorLabel.text = error ?? string.Empty;
            _errorLabel.EnableInClassList("irs-panel__error--hidden", string.IsNullOrEmpty(error));
        }

        private void UpdateOverlay(RecorderState state, in RecorderStatistics stats)
        {
            _overlayDot.EnableInClassList("irs-overlay__dot--buffering", state == RecorderState.Buffering);
            _overlayDot.EnableInClassList("irs-overlay__dot--recording", state == RecorderState.RecordingUnbounded);
            _overlayDot.EnableInClassList("irs-overlay__dot--busy", state == RecorderState.Exporting);
            _overlayDot.EnableInClassList("irs-overlay__dot--faulted", state == RecorderState.Faulted);
            _overlayDot.EnableInClassList("irs-overlay__dot--paused", _recorder.IsPaused);

            var pausedSuffix = _recorder.IsPaused ? " (paused)" : string.Empty;
            _overlayLabel.text = state switch
            {
                RecorderState.Buffering =>
                    $"{Formatting.Time(stats.BufferedDurationSeconds)} / ~{Formatting.Time(stats.EstimatedCapacitySeconds)}{pausedSuffix}",
                RecorderState.RecordingUnbounded => $"REC {Formatting.Time(stats.UnboundedElapsedSeconds)}{pausedSuffix}",
                RecorderState.Exporting => "Saving…",
                RecorderState.Faulted => "Error",
                _ => "Off"
            };
        }

        private void ApplySafeArea()
        {
            var panel = _root.panel;
            if (panel == null || Screen.width <= 0 || Screen.height <= 0) return;

            var safe = Screen.safeArea;
            var topLeft = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(safe.xMin, Screen.height - safe.yMax));
            var bottomRight =
                RuntimePanelUtils.ScreenToPanel(panel, new Vector2(safe.xMax, Screen.height - safe.yMin));
            var size = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(Screen.width, Screen.height));

            _root.style.paddingLeft = Mathf.Max(0, topLeft.x);
            _root.style.paddingTop = Mathf.Max(0, topLeft.y);
            _root.style.paddingRight = Mathf.Max(0, size.x - bottomRight.x);
            _root.style.paddingBottom = Mathf.Max(0, size.y - bottomRight.y);
        }

        private static string DescribePreset(RecordingPreset preset, in RecorderStatistics stats)
        {
            var context = PresetContext.FromCurrentEnvironment();
            var options = preset.ToEncodingOptions(context);
            var video = options.VideoOptions;
            var buffer = preset.Buffer;
            var bufferKind = buffer.Mode == BufferSpec.SpecMode.Disk ? "on disk" : "in memory";
            var estimate = stats.EstimatedCapacitySeconds > 0
                ? stats.EstimatedCapacitySeconds
                : preset.EstimateRecordableDurationSeconds(context);
            return string.Format(CultureInfo.InvariantCulture,
                "{0}×{1} @ {2} fps, {3:0.0} Mbps\nBuffer: {4} MiB {5} (≈ {6})",
                video.Width, video.Height, video.FpsHint, video.Bitrate / 1e6, buffer.GetCapacityBytes() >> 20,
                bufferKind, Formatting.Time(estimate));
        }

        private static int IndexOf(IReadOnlyList<RecordingPreset> presets, RecordingPreset target)
        {
            for (var i = 0; i < presets.Count; i++)
                if (presets[i] == target)
                    return i;
            return -1;
        }
    }
}
