// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Overlay and panel built in code, so that no UXML or prefab has to stay loadable across Unity versions.
    /// </summary>
    internal sealed class SuiteView : IDisposable
    {
        private const long RefreshIntervalMs = 250;

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
        private readonly IVisualElementScheduledItem _refresh;
        private readonly VisualElement _root;
        private readonly Transform _previewParent;
        private readonly Button _saveButton;
        private readonly Button _unboundedButton;

        private bool _busy;
        private bool _libraryTabSelected;
        private string _operationError;
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

            _bufferingToggle = new Toggle("Continuous buffering");
            _bufferingToggle.RegisterValueChangedCallback(e => Run(() => _recorder.SetBufferingEnabled(e.newValue)));
            content.Add(_bufferingToggle);

            var actions = new VisualElement();
            actions.AddToClassList("irs-panel__row");
            actions.AddToClassList("irs-panel__section");
            // Saving a replay always goes through the trim view, where the whole footage is selected initially.
            _saveButton = new Button(SaveReplay) { text = "Save replay" };
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
            SelectTab(false);
            Refresh();
        }

        public bool IsVisible
        {
            get => _root.style.display != DisplayStyle.None;
            set => _root.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void Dispose()
        {
            _refresh.Pause();
            _trim?.Dispose();
            _library.Dispose();
            _recorder.StateChanged -= OnRecorderChanged;
            _recorder.ConfigurationChanged -= OnRecorderChanged;
            _root.RemoveFromHierarchy();
        }

        private bool IsPanelOpen => !_panel.ClassListContains("irs-panel--hidden");

        private void TogglePanel()
        {
            _panel.ToggleInClassList("irs-panel--hidden");
            _library.SetVisible(IsPanelOpen && _libraryTabSelected);
        }

        private void SelectTab(bool library)
        {
            _libraryTabSelected = library;
            _recorderTabButton.EnableInClassList("irs-tab--selected", !library);
            _libraryTabButton.EnableInClassList("irs-tab--selected", library);
            _recorderTab.style.display = library ? DisplayStyle.None : DisplayStyle.Flex;
            _libraryTab.style.display = library ? DisplayStyle.Flex : DisplayStyle.None;
            _library.SetVisible(IsPanelOpen && library);
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
            if (_busy || _trim != null) return;
            _busy = true;
            _operationError = null;
            Refresh();
            try
            {
                var draft = await _recorder.CaptureReplayAsync();
                _trim = new TrimView(_root, draft, _previewParent);
                _trim.Closed += saved =>
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
            _saveButton.SetEnabled(!_busy && _trim == null && state == RecorderState.Buffering);
            _unboundedButton.text = state == RecorderState.RecordingUnbounded ? "Stop recording" : "Start recording";
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
