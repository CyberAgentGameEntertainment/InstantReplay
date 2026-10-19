// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay.DiskBufferTests
{
    /// <summary>
    ///     Drives the real platform encoder, persists every encoded frame through the disk buffer, reads the buffer back
    ///     from the files alone, and muxes the result into an MP4.
    /// </summary>
    /// <remarks>
    ///     This is the check that validates the premise of the feature: an encoded payload written to disk in one pass can
    ///     be muxed in a later pass with nothing but the files and the manifest, which is what crash recovery does. It is
    ///     skipped when the native library for the running platform is not present.
    /// </remarks>
    internal static class EndToEnd
    {
        private const int Width = 320;
        private const int Height = 240;
        private const int FrameRate = 30;
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const double Seconds = 2.0;

        private static bool? _isSupported;

        public static bool IsSupported
        {
            get
            {
                if (_isSupported is {} value) return value;

                bool result;
                try
                {
                    using var system = new EncodingSystem(VideoOptions(), AudioOptions());
                    result = true;
                }
                catch
                {
                    result = false;
                }

                _isSupported = result;
                return result;
            }
        }

        public static async Task<Result> RunAsync(string directory)
        {
            var manifestPath = Path.Combine(directory, DiskBufferFormat.ManifestFileName);
            var manifest = DiskBufferManifest.Create(VideoOptions(), AudioOptions(), "OSXEditor", "test", "test");
            var manifestBytes = manifest.Write(manifestPath);

            // --- pass one: encode and persist -------------------------------------------------
            var videoRecords = 0;
            var audioRecords = 0;
            var metadataRecords = 0;

            using (var writer = new DiskBufferSegmentWriter(directory, 64 * 1024 * 1024, 0.5, 4 * 1024 * 1024,
                       DiskBufferSyncMode.OperatingSystem))
            {
                writer.SetManifestBytes(manifestBytes);

                using var system = new EncodingSystem(VideoOptions(), AudioOptions());
                using var videoEncoder = system.CreateVideoEncoder();
                using var audioEncoder = system.CreateAudioEncoder();

                var writerLock = new object();

                async Task DrainVideoAsync()
                {
                    while (true)
                    {
                        var frame = await videoEncoder.PullFrameAsync();
                        if (frame.Data.IsEmpty) break;

                        using (frame)
                        {
                            lock (writerLock)
                            {
                                writer.Write(DiskBufferTrack.Video, frame);
                                if (frame.Kind == UniencSampleKind.Metadata) metadataRecords++;
                                else videoRecords++;
                            }
                        }
                    }
                }

                async Task DrainAudioAsync()
                {
                    while (true)
                    {
                        var frame = await audioEncoder.PullFrameAsync();
                        if (frame.Data.IsEmpty) break;

                        using (frame)
                        {
                            lock (writerLock)
                            {
                                writer.Write(DiskBufferTrack.Audio, frame);
                                if (frame.Kind == UniencSampleKind.Metadata) metadataRecords++;
                                else audioRecords++;
                            }
                        }
                    }
                }

                await Task.WhenAll(
                    ProduceVideoAsync(videoEncoder),
                    ProduceAudioAsync(audioEncoder),
                    DrainVideoAsync(),
                    DrainAudioAsync());
            }

            // --- pass two: recover from the files alone ---------------------------------------
            if (!DiskBufferManifest.TryRead(manifestPath, out var recovered))
                return new Result(false, "manifest could not be read back", 0, 0, 0, 0);

            var scan = DiskBufferSegmentReader.Scan(directory);
            var outputPath = Path.Combine(directory, "recovered.mp4");

            using (var selection = DiskBufferSegmentReader.BuildSelection(scan, null))
            {
                if (selection.IsEmpty)
                    return new Result(false, "no muxable video frames were recovered", videoRecords, audioRecords,
                        metadataRecords, 0);

                using var system = new EncodingSystem(recovered.ToVideoOptions(), recovered.ToAudioOptions());
                using var muxer = system.CreateMuxer(outputPath);
                await EncodedFrameMuxer.MuxAsync(muxer, selection.VideoFrames, selection.AudioFrames);
            }

            var size = new FileInfo(outputPath).Length;

            // --- pass three: corruption found while muxing ------------------------------------
            // A video payload is corrupted only after muxing has started, which can be observed only if payloads are
            // read while they are muxed rather than all at once beforehand. The video stream must end just before it.
            var truncatedPath = Path.Combine(directory, "truncated.mp4");
            var corruptIndex = FindRecordBeyondReadBuffer(scan.VideoSamples, scan.VideoSamples.Count / 2);
            int truncatedVideoFrames;

            using (var selection = DiskBufferSegmentReader.BuildSelection(scan, null))
            {
                if (selection.IsEmpty)
                    return new Result(false, "no muxable video frames were recovered for the truncation pass",
                        videoRecords, audioRecords, metadataRecords, size, outputPath);

                var video = new CorruptingReader(selection.VideoFrames, scan.VideoSamples[corruptIndex],
                    scan.VideoMetadata.Count + 1);

                using (var system = new EncodingSystem(recovered.ToVideoOptions(), recovered.ToAudioOptions()))
                using (var muxer = system.CreateMuxer(truncatedPath))
                {
                    await EncodedFrameMuxer.MuxAsync(muxer, video, selection.AudioFrames);
                }

                truncatedVideoFrames = video.SampleCount;
            }

            return new Result(true, null, videoRecords, audioRecords, metadataRecords, size, outputPath)
            {
                CorruptVideoIndex = corruptIndex,
                TruncatedVideoFrames = truncatedVideoFrames,
                TruncatedOutputBytes = new FileInfo(truncatedPath).Length
            };
        }

        /// <summary>
        ///     Finds a record at or after the given index that lies outside the window a file stream may already have
        ///     buffered while reading the first record, so that a corruption written to it is visible to the reader.
        /// </summary>
        private static int FindRecordBeyondReadBuffer(IReadOnlyList<DiskBufferScannedRecord> records, int start)
        {
            const long margin = 256 * 1024;

            var first = records[0];
            for (var i = Math.Max(start, 1); i < records.Count; i++)
                if (records[i].FilePath != first.FilePath || records[i].PayloadOffset - first.PayloadOffset > margin)
                    return i;

            return start;
        }

        private static async Task ProduceVideoAsync(VideoEncoder encoder)
        {
            const int frameBytes = Width * Height * 4;
            using var pool = new SharedBufferPool(frameBytes * 4);

            var total = (int)(FrameRate * Seconds);
            for (var i = 0; i < total; i++)
            {
                SharedBuffer<SpanWrapper> buffer;
                while (!pool.TryAlloc(frameBytes, out buffer)) Thread.Yield();

                using (buffer)
                {
                    // Filled in a separate method: a ref struct local may not stay alive across an await.
                    Fill(buffer, i);
                    await encoder.PushFrameAsync(buffer, Width, Height, (double)i / FrameRate);
                }
            }

            encoder.CompleteInput();
        }

        /// <summary>
        ///     Writes a moving gradient, so that successive frames genuinely differ and inter frames are produced.
        /// </summary>
        private static void Fill(SharedBuffer<SpanWrapper> buffer, int frameIndex)
        {
            var span = buffer.Value.UnsafeGetSpan();
            for (var p = 0; p < span.Length; p += 4)
            {
                span[p] = (byte)(p + frameIndex * 7);
                span[p + 1] = (byte)(frameIndex * 3);
                span[p + 2] = (byte)(p / 4);
                span[p + 3] = 255;
            }
        }

        private static async Task ProduceAudioAsync(AudioEncoder encoder)
        {
            var buffer = ArrayPool<short>.Shared.Rent(1024);
            try
            {
                var totalSamples = (int)Math.Ceiling(Seconds * SampleRate);
                for (var i = 0; i < totalSamples;)
                {
                    var remaining = (totalSamples - i) * Channels;
                    var block = buffer.AsMemory(0, Math.Min(buffer.Length, remaining));
                    var numSamples = block.Length / Channels;

                    for (var j = 0; j < numSamples; j++)
                    {
                        var t = (double)(i + j) / SampleRate;
                        var value = (short)(Math.Sin(2.0 * Math.PI * 440.0 * t) * short.MaxValue);
                        for (var c = 0; c < Channels; c++) block.Span[j * Channels + c] = value;
                    }

                    await encoder.PushSamplesAsync(block, (ulong)i);
                    i += numSamples;
                }
            }
            finally
            {
                ArrayPool<short>.Shared.Return(buffer);
            }

            encoder.CompleteInput();
        }

        private static VideoEncoderOptions VideoOptions()
        {
            return new VideoEncoderOptions
            {
                Width = Width,
                Height = Height,
                FpsHint = FrameRate,
                Bitrate = 1000000
            };
        }

        private static AudioEncoderOptions AudioOptions()
        {
            return new AudioEncoderOptions
            {
                SampleRate = SampleRate,
                Channels = Channels,
                Bitrate = 128000
            };
        }

        /// <summary>
        ///     Passes frames through and corrupts the payload of a record on disk once a given number of frames have been
        ///     read, so that the corruption can only be detected by reading that happens after muxing has begun.
        /// </summary>
        private sealed class CorruptingReader : IEncodedFrameReader
        {
            private readonly IEncodedFrameReader _inner;
            private readonly DiskBufferScannedRecord _target;
            private readonly int _triggerAfter;
            private bool _corrupted;
            private int _read;

            public CorruptingReader(IEncodedFrameReader inner, DiskBufferScannedRecord target, int triggerAfter)
            {
                _inner = inner;
                _target = target;
                _triggerAfter = triggerAfter;
            }

            /// <summary>
            ///     Number of samples returned, excluding codec configuration.
            /// </summary>
            public int SampleCount { get; private set; }

            public bool TryReadNext(out EncodedFrame frame)
            {
                if (!_inner.TryReadNext(out frame)) return false;

                if (frame.Kind != UniencSampleKind.Metadata) SampleCount++;

                if (++_read >= _triggerAfter && !_corrupted)
                {
                    _corrupted = true;
                    using var stream = new FileStream(_target.FilePath, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.ReadWrite);
                    stream.Position = _target.PayloadOffset;
                    var b = stream.ReadByte();
                    stream.Position = _target.PayloadOffset;
                    stream.WriteByte((byte)(b ^ 0xFF));
                }

                return true;
            }

            public void Dispose()
            {
                _inner.Dispose();
            }
        }

        internal struct Result
        {
            public readonly bool Success;
            public readonly string Error;
            public readonly int VideoRecords;
            public readonly int AudioRecords;
            public readonly int MetadataRecords;
            public readonly long OutputBytes;
            public readonly string OutputPath;
            public int CorruptVideoIndex;
            public int TruncatedVideoFrames;
            public long TruncatedOutputBytes;

            public Result(bool success, string error, int videoRecords, int audioRecords, int metadataRecords,
                long outputBytes, string outputPath = null)
            {
                Success = success;
                Error = error;
                VideoRecords = videoRecords;
                AudioRecords = audioRecords;
                MetadataRecords = metadataRecords;
                OutputBytes = outputBytes;
                OutputPath = outputPath;
                CorruptVideoIndex = 0;
                TruncatedVideoFrames = 0;
                TruncatedOutputBytes = 0;
            }
        }
    }
}
