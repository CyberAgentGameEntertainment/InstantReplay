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
    ///     Fields that override individual properties of the active preset. Each change applies a modified copy of the
    ///     active preset, which the recorder persists as a customized preset.
    /// </summary>
    internal sealed class PresetEditorView
    {
        private const string ScreenRelative = "Screen relative";
        private const string FixedSize = "Fixed size";
        private const string BitsPerPixel = "Bits per pixel";
        private const string FixedBitrate = "Fixed";
        private const string InMemory = "Memory";
        private const string OnDisk = "Disk";

        private readonly IntegerField _audioBitrateKbps;
        private readonly DropdownField _bitrateMode;
        private readonly FloatField _bitsPerPixel;
        private readonly DropdownField _bufferMode;
        private readonly IntegerField _bufferMegabytes;
        private readonly FloatField _fixedBitrateMbps;
        private readonly IntegerField _fixedHeight;
        private readonly IntegerField _fixedWidth;
        private readonly Toggle _forceReadback;
        private readonly IntegerField _frameRate;
        private readonly IntegerField _maxLongSide;
        private readonly InstantReplayRecorder _recorder;
        private readonly Button _resetButton;
        private readonly DropdownField _resolutionMode;
        private readonly Action<Action> _run;
        private readonly FloatField _scale;

        public PresetEditorView(InstantReplayRecorder recorder, Action<Action> run)
        {
            _recorder = recorder;
            _run = run;

            Root = new Foldout { text = "Customize", value = false };
            Root.AddToClassList("irs-preset-editor");

            _resolutionMode = AddDropdown("Resolution", new List<string> { ScreenRelative, FixedSize },
                (p, v) => p.Resolution = v == FixedSize
                    ? ResolutionSpec.Fixed(Math.Max(2, p.Resolution.Width > 0 ? p.Resolution.Width : 1280),
                        Math.Max(2, p.Resolution.Height > 0 ? p.Resolution.Height : 720))
                    : ResolutionSpec.ScreenRelative(p.Resolution.Scale > 0 ? p.Resolution.Scale : 1f,
                        p.Resolution.MaxLongSide));
            _scale = AddFloat("Scale",
                (p, v) => p.Resolution = ResolutionSpec.ScreenRelative(Mathf.Clamp(v, 0.05f, 4f),
                    p.Resolution.MaxLongSide));
            _maxLongSide = AddInteger("Max long side",
                (p, v) => p.Resolution = ResolutionSpec.ScreenRelative(p.Resolution.Scale, Math.Max(0, v)));
            _fixedWidth = AddInteger("Width",
                (p, v) => p.Resolution = ResolutionSpec.Fixed(Math.Max(2, v), p.Resolution.Height));
            _fixedHeight = AddInteger("Height",
                (p, v) => p.Resolution = ResolutionSpec.Fixed(p.Resolution.Width, Math.Max(2, v)));

            _frameRate = AddInteger("Frame rate", (p, v) => p.FrameRate = Mathf.Clamp(v, 1, 240));

            _bitrateMode = AddDropdown("Video bitrate", new List<string> { BitsPerPixel, FixedBitrate },
                (p, v) => p.VideoBitrate = v == FixedBitrate
                    ? BitrateSpec.Fixed(ResolveBitrate(p))
                    : BitrateSpec.BitsPerPixel(p.VideoBitrate.BitsPerPixelValue > 0
                        ? p.VideoBitrate.BitsPerPixelValue
                        : 0.1f));
            _bitsPerPixel = AddFloat("Bits per pixel",
                (p, v) => p.VideoBitrate = BitrateSpec.BitsPerPixel(Mathf.Clamp(v, 0.001f, 10f)));
            _fixedBitrateMbps = AddFloat("Mbps",
                (p, v) => p.VideoBitrate = BitrateSpec.Fixed((int)Math.Round(Mathf.Clamp(v, 0.05f, 100f) * 1e6)));

            _audioBitrateKbps = AddInteger("Audio kbps", (p, v) => p.AudioBitrate = Mathf.Clamp(v, 16, 512) * 1000);

            _bufferMode = AddDropdown("Buffer", new List<string> { InMemory, OnDisk },
                (p, v) => p.Buffer = v == OnDisk
                    ? BufferSpec.Disk(p.Buffer.MaxMegabytes)
                    : BufferSpec.InMemory(p.Buffer.MaxMegabytes));
            _bufferMegabytes = AddInteger("Buffer MiB",
                (p, v) => p.Buffer = p.Buffer.Mode == BufferSpec.SpecMode.Disk
                    ? BufferSpec.Disk(Math.Max(1, v))
                    : BufferSpec.InMemory(Math.Max(1, v)));

            _forceReadback = new Toggle("Force readback").AsSwitch();
            _forceReadback.RegisterValueChangedCallback(e => Apply(p => p.ForceReadback = e.newValue));
            Root.Add(_forceReadback);

            _resetButton = new Button(ResetToBase) { text = "Reset to preset" }.WithClass(Controls.Ghost);
            Root.Add(_resetButton);
        }

        public Foldout Root { get; }

        public void Refresh(bool editable)
        {
            var preset = _recorder.ActivePreset;
            Root.SetEnabled(editable);

            var resolution = preset.Resolution;
            var isFixed = resolution.Mode == ResolutionSpec.SpecMode.Fixed;
            _resolutionMode.SetValueWithoutNotify(isFixed ? FixedSize : ScreenRelative);
            SetVisible(_scale, !isFixed);
            SetVisible(_maxLongSide, !isFixed);
            SetVisible(_fixedWidth, isFixed);
            SetVisible(_fixedHeight, isFixed);
            SetIfIdle(_scale, resolution.Scale);
            SetIfIdle(_maxLongSide, resolution.MaxLongSide);
            SetIfIdle(_fixedWidth, resolution.Width);
            SetIfIdle(_fixedHeight, resolution.Height);

            SetIfIdle(_frameRate, preset.FrameRate);

            var bitrate = preset.VideoBitrate;
            var isFixedBitrate = bitrate.Mode == BitrateSpec.SpecMode.Fixed;
            _bitrateMode.SetValueWithoutNotify(isFixedBitrate ? FixedBitrate : BitsPerPixel);
            SetVisible(_bitsPerPixel, !isFixedBitrate);
            SetVisible(_fixedBitrateMbps, isFixedBitrate);
            SetIfIdle(_bitsPerPixel, bitrate.BitsPerPixelValue);
            SetIfIdle(_fixedBitrateMbps, (float)(bitrate.BitsPerSecond / 1e6));

            SetIfIdle(_audioBitrateKbps, preset.AudioBitrate / 1000);

            _bufferMode.SetValueWithoutNotify(preset.Buffer.Mode == BufferSpec.SpecMode.Disk ? OnDisk : InMemory);
            SetIfIdle(_bufferMegabytes, preset.Buffer.MaxMegabytes);

            _forceReadback.SetValueWithoutNotify(preset.ForceReadback);
            _resetButton.SetEnabled(_recorder.IsActivePresetCustomized && FindBase(preset) != null);
        }

        private DropdownField AddDropdown(string label, List<string> choices, Action<RecordingPreset, string> apply)
        {
            var field = new DropdownField(label, choices, 0);
            field.RegisterValueChangedCallback(e => Apply(p => apply(p, e.newValue)));
            Root.Add(field);
            return field;
        }

        private IntegerField AddInteger(string label, Action<RecordingPreset, int> apply)
        {
            var field = new IntegerField(label) { isDelayed = true };
            field.RegisterValueChangedCallback(e => Apply(p => apply(p, e.newValue)));
            Root.Add(field);
            return field;
        }

        private FloatField AddFloat(string label, Action<RecordingPreset, float> apply)
        {
            var field = new FloatField(label) { isDelayed = true };
            field.RegisterValueChangedCallback(e => Apply(p => apply(p, e.newValue)));
            Root.Add(field);
            return field;
        }

        private void Apply(Action<RecordingPreset> modify)
        {
            _run(() =>
            {
                var basePreset = FindBase(_recorder.ActivePreset);
                var preset = _recorder.ActivePreset.Clone();
                modify(preset);
                if (basePreset != null) preset.DisplayName = basePreset.DisplayName + " (custom)";
                _recorder.ApplyPreset(preset);
            });
        }

        private void ResetToBase()
        {
            var basePreset = FindBase(_recorder.ActivePreset);
            if (basePreset != null) _run(() => _recorder.ApplyPreset(basePreset));
        }

        private RecordingPreset FindBase(RecordingPreset preset)
        {
            foreach (var candidate in _recorder.Presets)
                if (candidate.Id == preset.Id)
                    return candidate;
            return null;
        }

        private static int ResolveBitrate(RecordingPreset preset)
        {
            var options = preset.ToEncodingOptions(PresetContext.FromCurrentEnvironment());
            return (int)Math.Min(int.MaxValue, options.VideoOptions.Bitrate);
        }

        private static void SetVisible(VisualElement element, bool visible)
        {
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // A field being edited is left alone, so that the periodic refresh does not overwrite the user's input.
        private static void SetIfIdle<T>(BaseField<T> field, T value)
        {
            if (field.focusController?.focusedElement is VisualElement focused && field.Contains(focused)) return;
            field.SetValueWithoutNotify(value);
        }
    }
}
