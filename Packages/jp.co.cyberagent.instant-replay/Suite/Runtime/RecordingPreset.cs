// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UniEnc;
using UnityEngine;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     Named set of recording parameters that a project defines in <see cref="InstantReplaySuiteSettings" /> and that
    ///     the user chooses from at runtime.
    /// </summary>
    [Serializable]
    public sealed class RecordingPreset
    {
        [SerializeField] private string id = "default";
        [SerializeField] private string displayName = "Default";
        [SerializeField] private ResolutionSpec resolution = ResolutionSpec.ScreenRelative(1f, 1280);
        [SerializeField] private int frameRate = 30;
        [SerializeField] private BitrateSpec videoBitrate = BitrateSpec.BitsPerPixel(0.1f);
        [SerializeField] private int audioBitrate = 128000;
        [SerializeField] private BufferSpec buffer = BufferSpec.InMemory(64);
        [SerializeField] private bool forceReadback;

        /// <summary>
        ///     Identifier that stays the same across builds. The selected preset is persisted by this value.
        /// </summary>
        public string Id
        {
            get => id;
            set => id = value;
        }

        /// <summary>
        ///     Name shown in the user interface.
        /// </summary>
        public string DisplayName
        {
            get => string.IsNullOrEmpty(displayName) ? id : displayName;
            set => displayName = value;
        }

        public ResolutionSpec Resolution
        {
            get => resolution;
            set => resolution = value;
        }

        /// <summary>
        ///     Frame rate of the recording. Frames are resampled to this rate.
        /// </summary>
        public int FrameRate
        {
            get => frameRate;
            set => frameRate = value;
        }

        public BitrateSpec VideoBitrate
        {
            get => videoBitrate;
            set => videoBitrate = value;
        }

        /// <summary>
        ///     Audio bitrate in bits per second.
        /// </summary>
        public int AudioBitrate
        {
            get => audioBitrate;
            set => audioBitrate = value;
        }

        public BufferSpec Buffer
        {
            get => buffer;
            set => buffer = value;
        }

        /// <summary>
        ///     Corresponds to <see cref="RealtimeEncodingOptions.ForceReadback" />.
        /// </summary>
        public bool ForceReadback
        {
            get => forceReadback;
            set => forceReadback = value;
        }

        /// <summary>
        ///     Converts the preset to encoding options for the given environment.
        /// </summary>
        public RealtimeEncodingOptions ToEncodingOptions(in PresetContext context)
        {
            var fps = Mathf.Max(1, frameRate);
            resolution.Resolve(context, out var width, out var height);

            var options = RealtimeEncodingOptions.Default;
            options.VideoOptions = new VideoEncoderOptions
            {
                Width = (uint)width,
                Height = (uint)height,
                FpsHint = (uint)fps,
                Bitrate = (uint)videoBitrate.Resolve(width, height, fps)
            };
            options.AudioOptions = new AudioEncoderOptions
            {
                SampleRate = (uint)Mathf.Max(1, context.AudioSampleRate),
                Channels = 2,
                Bitrate = (uint)Mathf.Max(1, audioBitrate)
            };
            options.FixedFrameRate = fps;
            options.ForceReadback = forceReadback;
            buffer.Apply(ref options);
            return options;
        }

        /// <summary>
        ///     Estimates how many seconds the buffer can hold, assuming the encoder meets the configured bitrates.
        /// </summary>
        public double EstimateRecordableDurationSeconds(in PresetContext context)
        {
            var options = ToEncodingOptions(context);
            var bitsPerSecond = (double)options.VideoOptions.Bitrate + options.AudioOptions.Bitrate;
            return bitsPerSecond <= 0 ? 0 : buffer.GetCapacityBytes() * 8.0 / bitsPerSecond;
        }

        /// <summary>
        ///     Creates the preset used when the settings define none.
        /// </summary>
        public static RecordingPreset CreateDefault()
        {
            return new RecordingPreset();
        }
    }

    /// <summary>
    ///     Output resolution of a <see cref="RecordingPreset" />.
    /// </summary>
    [Serializable]
    public struct ResolutionSpec
    {
        public enum SpecMode
        {
            Fixed,
            ScreenRelative
        }

        [SerializeField] private SpecMode mode;
        [SerializeField] private int width;
        [SerializeField] private int height;
        [SerializeField] private float scale;
        [SerializeField] private int maxLongSide;

        public SpecMode Mode => mode;
        public int Width => width;
        public int Height => height;
        public float Scale => scale;
        public int MaxLongSide => maxLongSide;

        /// <summary>
        ///     A fixed resolution regardless of the screen.
        /// </summary>
        public static ResolutionSpec Fixed(int width, int height)
        {
            return new ResolutionSpec { mode = SpecMode.Fixed, width = width, height = height };
        }

        /// <summary>
        ///     The screen resolution multiplied by <paramref name="scale" />, then shrunk so that the longer side does not
        ///     exceed <paramref name="maxLongSide" />. Zero or less leaves the longer side unbounded.
        /// </summary>
        public static ResolutionSpec ScreenRelative(float scale, int maxLongSide = 0)
        {
            return new ResolutionSpec { mode = SpecMode.ScreenRelative, scale = scale, maxLongSide = maxLongSide };
        }

        /// <summary>
        ///     Computes the resolution. Both dimensions are even and at least 2, as required by the encoders.
        /// </summary>
        public void Resolve(in PresetContext context, out int resolvedWidth, out int resolvedHeight)
        {
            double w, h;
            if (mode == SpecMode.Fixed)
            {
                w = width;
                h = height;
            }
            else
            {
                var s = scale > 0 ? scale : 1f;
                w = context.ScreenWidth * s;
                h = context.ScreenHeight * s;
                var longSide = Math.Max(w, h);
                if (maxLongSide > 0 && longSide > maxLongSide)
                {
                    var shrink = maxLongSide / longSide;
                    w *= shrink;
                    h *= shrink;
                }
            }

            resolvedWidth = ToEven(w);
            resolvedHeight = ToEven(h);
        }

        private static int ToEven(double value)
        {
            var rounded = (int)Math.Round(value);
            return Math.Max(2, rounded - (rounded & 1));
        }
    }

    /// <summary>
    ///     Video bitrate of a <see cref="RecordingPreset" />.
    /// </summary>
    [Serializable]
    public struct BitrateSpec
    {
        public enum SpecMode
        {
            Fixed,
            BitsPerPixel
        }

        [SerializeField] private SpecMode mode;
        [SerializeField] private int bitsPerSecond;
        [SerializeField] private float bitsPerPixel;

        public SpecMode Mode => mode;
        public int BitsPerSecond => bitsPerSecond;
        public float BitsPerPixelValue => bitsPerPixel;

        /// <summary>
        ///     A fixed bitrate in bits per second.
        /// </summary>
        public static BitrateSpec Fixed(int bitsPerSecond)
        {
            return new BitrateSpec { mode = SpecMode.Fixed, bitsPerSecond = bitsPerSecond };
        }

        /// <summary>
        ///     A bitrate proportional to the number of pixels per second: width × height × frame rate × bits per pixel.
        /// </summary>
        public static BitrateSpec BitsPerPixel(float bitsPerPixel)
        {
            return new BitrateSpec { mode = SpecMode.BitsPerPixel, bitsPerPixel = bitsPerPixel };
        }

        /// <summary>
        ///     Computes the bitrate in bits per second for the given output.
        /// </summary>
        public long Resolve(int width, int height, int frameRate)
        {
            var value = mode == SpecMode.Fixed
                ? bitsPerSecond
                : (long)Math.Round((double)width * height * frameRate * bitsPerPixel);
            return Math.Max(1, Math.Min(uint.MaxValue, value));
        }
    }

    /// <summary>
    ///     Where a <see cref="RecordingPreset" /> keeps the encoded frames while buffering, and how much.
    /// </summary>
    [Serializable]
    public struct BufferSpec
    {
        public enum SpecMode
        {
            InMemory,
            Disk
        }

        private const long BytesPerMegabyte = 1024 * 1024;

        [SerializeField] private SpecMode mode;
        [SerializeField] private int maxMegabytes;

        public SpecMode Mode => mode;
        public int MaxMegabytes => maxMegabytes;

        /// <summary>
        ///     Keeps up to <paramref name="maxMegabytes" /> MiB of encoded frames in memory.
        /// </summary>
        public static BufferSpec InMemory(int maxMegabytes)
        {
            return new BufferSpec { mode = SpecMode.InMemory, maxMegabytes = maxMegabytes };
        }

        /// <summary>
        ///     Keeps up to <paramref name="maxMegabytes" /> MiB of encoded frames on disk, based on
        ///     <see cref="DiskBufferOptions.Default" />.
        /// </summary>
        public static BufferSpec Disk(int maxMegabytes)
        {
            return new BufferSpec { mode = SpecMode.Disk, maxMegabytes = maxMegabytes };
        }

        /// <summary>
        ///     Upper bound of the buffer in bytes after clamping.
        /// </summary>
        public long GetCapacityBytes()
        {
            var bytes = Math.Max(1, maxMegabytes) * BytesPerMegabyte;
            return mode == SpecMode.Disk ? Math.Max(DiskBufferOptions.MinimumDiskUsageBytes, bytes) : bytes;
        }

        internal void Apply(ref RealtimeEncodingOptions options)
        {
            var capacity = GetCapacityBytes();
            if (mode == SpecMode.Disk)
            {
                var disk = DiskBufferOptions.Default;
                disk.MaxDiskUsageBytes = capacity;
                // Keep several segments within the bound, so that evicting one does not discard most of the buffer.
                disk.MaxSegmentBytes = Math.Max(1, Math.Min(disk.MaxSegmentBytes, capacity / 4));
                options.DiskBuffer = disk;
            }
            else
            {
                options.MaxMemoryUsageBytesForCompressedFrames = capacity;
                options.DiskBuffer = null;
            }
        }
    }

    /// <summary>
    ///     Environment that a <see cref="RecordingPreset" /> is resolved against.
    /// </summary>
    public readonly struct PresetContext
    {
        public int ScreenWidth { get; }
        public int ScreenHeight { get; }
        public int AudioSampleRate { get; }

        public PresetContext(int screenWidth, int screenHeight, int audioSampleRate)
        {
            ScreenWidth = screenWidth;
            ScreenHeight = screenHeight;
            AudioSampleRate = audioSampleRate;
        }

        /// <summary>
        ///     Captures the current screen size and the output sample rate of the Unity audio system. Call it on the main
        ///     thread.
        /// </summary>
        public static PresetContext FromCurrentEnvironment()
        {
            return new PresetContext(Screen.width, Screen.height, AudioSettings.outputSampleRate);
        }
    }
}
