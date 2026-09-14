// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantReplay;
using UniEnc;

namespace InstantReplay.DiskBufferTests
{
    /// <summary>
    ///     Exercises the storage layer of the disk buffer without the Unity Editor. Returns a non-zero exit code when a
    ///     check fails.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            Run("record round trip", RecordRoundTrip);
            Run("torn tail is truncated", TornTailIsTruncated);
            Run("torn header is truncated", TornHeaderIsTruncated);
            Run("corrupt payload stops materialization", CorruptPayloadStopsMaterialization);
            Run("disk usage stays within the hard bound", DiskUsageStaysWithinHardBound);
            Run("metadata survives segment eviction", MetadataSurvivesEviction);
            Run("duplicate metadata is stored once", DuplicateMetadataStoredOnce);
            Run("segments start at key frames", SegmentsStartAtKeyFrames);
            Run("manifest round trip", ManifestRoundTrip);
            Run("selection starts at the nearest key frame", SelectionStartsAtNearestKeyFrame);
            RunEndToEnd();

            Console.WriteLine(_failures == 0
                ? "\nAll checks passed."
                : $"\n{_failures} check(s) failed.");
            return _failures == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- checks

        private static void RecordRoundTrip(string directory)
        {
            var payloads = new List<byte[]>();

            using (var writer = CreateWriter(directory))
            {
                writer.SetManifestBytes(0);
                Write(writer, DiskBufferTrack.Video, UniencSampleKind.Metadata, 0.0, Payload(7, 0xAA));

                for (var i = 0; i < 10; i++)
                {
                    var payload = Payload(100 + i, (byte)i);
                    payloads.Add(payload);
                    Write(writer, DiskBufferTrack.Video,
                        i % 5 == 0 ? UniencSampleKind.Key : UniencSampleKind.Interpolated, i * 0.1, payload);
                }

                for (var i = 0; i < 4; i++)
                    Write(writer, DiskBufferTrack.Audio, UniencSampleKind.Key, i * 0.25, Payload(32, (byte)(i + 100)));
            }

            var scan = DiskBufferSegmentReader.Scan(directory);

            AssertEqual(10, scan.VideoSamples.Count, "video sample count");
            AssertEqual(4, scan.AudioSamples.Count, "audio sample count");
            AssertEqual(1, scan.VideoMetadata.Count, "video metadata count");
            AssertEqual(0, scan.AudioMetadata.Count, "audio metadata count");
            AssertTrue(Math.Abs(scan.LatestVideoTimestamp - 0.9) < 1e-9, "latest video timestamp");

            var frames = DiskBufferSegmentReader.Materialize(scan.VideoSamples);
            AssertEqual(10, frames.Length, "materialized frame count");

            for (var i = 0; i < frames.Length; i++)
            {
                AssertTrue(frames[i].Data.SequenceEqual(payloads[i]), $"payload {i} round trip");
                AssertTrue(Math.Abs(frames[i].Timestamp - i * 0.1) < 1e-9, $"timestamp {i}");
                AssertEqual(i % 5 == 0 ? UniencSampleKind.Key : UniencSampleKind.Interpolated, frames[i].Kind,
                    $"kind {i}");
            }

            foreach (var frame in frames) frame.Dispose();
        }

        private static void TornTailIsTruncated(string directory)
        {
            using (var writer = CreateWriter(directory))
            {
                writer.SetManifestBytes(0);
                for (var i = 0; i < 6; i++)
                    Write(writer, DiskBufferTrack.Video, UniencSampleKind.Key, i * 0.1, Payload(64, (byte)i));
            }

            var segment = DiskBufferSegmentReader.EnumerateSegmentFiles(directory).Last();
            var full = new FileInfo(segment).Length;

            // Cut the file in the middle of the payload of the last record.
            Truncate(segment, full - 20);

            var scan = DiskBufferSegmentReader.Scan(directory);
            AssertEqual(5, scan.VideoSamples.Count, "records before the tear are recovered");

            var frames = DiskBufferSegmentReader.Materialize(scan.VideoSamples);
            AssertEqual(5, frames.Length, "torn record is not materialized");
            foreach (var frame in frames) frame.Dispose();
        }

        private static void TornHeaderIsTruncated(string directory)
        {
            using (var writer = CreateWriter(directory))
            {
                writer.SetManifestBytes(0);
                for (var i = 0; i < 4; i++)
                    Write(writer, DiskBufferTrack.Video, UniencSampleKind.Key, i * 0.1, Payload(64, (byte)i));
            }

            var segment = DiskBufferSegmentReader.EnumerateSegmentFiles(directory).Last();
            var full = new FileInfo(segment).Length;

            // Cut the file in the middle of the 20-byte header of the last record.
            Truncate(segment, full - 64 - 10);

            var scan = DiskBufferSegmentReader.Scan(directory);
            AssertEqual(3, scan.VideoSamples.Count, "records before the torn header are recovered");
        }

        private static void CorruptPayloadStopsMaterialization(string directory)
        {
            using (var writer = CreateWriter(directory))
            {
                writer.SetManifestBytes(0);
                for (var i = 0; i < 5; i++)
                    Write(writer, DiskBufferTrack.Video, UniencSampleKind.Key, i * 0.1, Payload(64, (byte)i));
            }

            var scan = DiskBufferSegmentReader.Scan(directory);
            AssertEqual(5, scan.VideoSamples.Count, "all records scanned");

            // Flip one byte inside the payload of the third record. The header stays intact, so only the checksum can
            // detect it.
            var third = scan.VideoSamples[2];
            using (var stream = new FileStream(third.FilePath, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.Position = third.PayloadOffset + 5;
                var b = stream.ReadByte();
                stream.Position = third.PayloadOffset + 5;
                stream.WriteByte((byte)(b ^ 0xFF));
            }

            var frames = DiskBufferSegmentReader.Materialize(scan.VideoSamples);
            AssertEqual(2, frames.Length, "materialization stops at the corrupt record");
            foreach (var frame in frames) frame.Dispose();
        }

        private static void DiskUsageStaysWithinHardBound(string directory)
        {
            const long max = 512 * 1024;
            const int payloadSize = 8 * 1024;

            using (var writer = new DiskBufferSegmentWriter(directory, max, 0.5, 64 * 1024,
                       DiskBufferSyncMode.OperatingSystem))
            {
                writer.SetManifestBytes(0);
                Write(writer, DiskBufferTrack.Video, UniencSampleKind.Metadata, 0.0, Payload(48, 0x5A));

                // Write far more than the bound allows, so eviction must run many times over.
                for (var i = 0; i < 400; i++)
                {
                    Write(writer, DiskBufferTrack.Video, i % 4 == 0 ? UniencSampleKind.Key
                        : UniencSampleKind.Interpolated, i * 0.05, Payload(payloadSize, (byte)i));

                    AssertTrue(writer.TotalBytes <= max,
                        $"tracked size {writer.TotalBytes} stays within {max} at write {i}");

                    if (i % 25 != 0) continue;

                    writer.FlushToOperatingSystem();
                    var actual = DirectorySize(directory);
                    AssertTrue(actual <= max, $"actual size {actual} stays within {max} at write {i}");
                }
            }

            var final = DirectorySize(directory);
            AssertTrue(final <= max, $"final size {final} stays within {max}");
            AssertTrue(final > max / 4, $"final size {final} still uses a useful part of the budget");

            // The buffer must still hold something muxable after all that eviction.
            var scan = DiskBufferSegmentReader.Scan(directory);
            AssertTrue(scan.VideoSamples.Count > 0, "records remain after eviction");
            AssertTrue(scan.VideoMetadata.Count == 1, "metadata survives eviction");
        }

        private static void MetadataSurvivesEviction(string directory)
        {
            const long max = 256 * 1024;
            var metadataPayload = Payload(37, 0xC3);

            using (var writer = new DiskBufferSegmentWriter(directory, max, 0.2, 32 * 1024,
                       DiskBufferSyncMode.OperatingSystem))
            {
                writer.SetManifestBytes(0);
                Write(writer, DiskBufferTrack.Video, UniencSampleKind.Metadata, 0.0, metadataPayload);
                Write(writer, DiskBufferTrack.Audio, UniencSampleKind.Metadata, 0.0, Payload(5, 0x11));

                for (var i = 0; i < 300; i++)
                    Write(writer, DiskBufferTrack.Video, i % 3 == 0 ? UniencSampleKind.Key
                        : UniencSampleKind.Interpolated, i * 0.05, Payload(4 * 1024, (byte)i));
            }

            var scan = DiskBufferSegmentReader.Scan(directory);
            AssertEqual(1, scan.VideoMetadata.Count, "video metadata still present");
            AssertEqual(1, scan.AudioMetadata.Count, "audio metadata still present");

            var frames = DiskBufferSegmentReader.Materialize(scan.VideoMetadata);
            AssertEqual(1, frames.Length, "video metadata materializes");
            AssertTrue(frames[0].Data.SequenceEqual(metadataPayload), "video metadata payload is intact");
            AssertEqual(UniencSampleKind.Metadata, frames[0].Kind, "video metadata kind");
            foreach (var frame in frames) frame.Dispose();
        }

        private static void DuplicateMetadataStoredOnce(string directory)
        {
            var payload = Payload(24, 0x77);

            using (var writer = CreateWriter(directory))
            {
                writer.SetManifestBytes(0);
                for (var i = 0; i < 50; i++)
                    Write(writer, DiskBufferTrack.Video, UniencSampleKind.Metadata, i * 0.1, payload);

                // A genuinely different configuration is kept alongside the first.
                Write(writer, DiskBufferTrack.Video, UniencSampleKind.Metadata, 5.0, Payload(24, 0x78));
                Write(writer, DiskBufferTrack.Video, UniencSampleKind.Key, 0.0, Payload(64, 1));
            }

            var scan = DiskBufferSegmentReader.Scan(directory);
            AssertEqual(2, scan.VideoMetadata.Count, "identical metadata is written once");
        }

        private static void SegmentsStartAtKeyFrames(string directory)
        {
            using (var writer = new DiskBufferSegmentWriter(directory, 64 * 1024 * 1024, 0.5, 1024 * 1024,
                       DiskBufferSyncMode.OperatingSystem))
            {
                writer.SetManifestBytes(0);
                for (var i = 0; i < 120; i++)
                    Write(writer, DiskBufferTrack.Video, i % 10 == 0 ? UniencSampleKind.Key
                        : UniencSampleKind.Interpolated, i * 0.1, Payload(512, (byte)i));
            }

            var segments = DiskBufferSegmentReader.EnumerateSegmentFiles(directory);
            AssertTrue(segments.Count > 1, "the stream rotated into several segments");

            foreach (var segment in segments)
            {
                var scan = DiskBufferSegmentReader.Scan(Path.GetDirectoryName(segment));
                var first = scan.VideoSamples.First(r => r.FilePath == segment);
                AssertEqual(UniencSampleKind.Key, first.Kind, $"{Path.GetFileName(segment)} starts at a key frame");
            }
        }

        private static void ManifestRoundTrip(string directory)
        {
            var manifest = DiskBufferManifest.Create(
                new VideoEncoderOptions { Width = 1280, Height = 720, FpsHint = 30, Bitrate = 2500000 },
                new AudioEncoderOptions { SampleRate = 44100, Channels = 2, Bitrate = 128000 },
                "Android", "2022.3.0f1", "1.0.0-\"quoted\"\\slash");

            var path = Path.Combine(directory, DiskBufferFormat.ManifestFileName);
            var written = manifest.Write(path);
            AssertTrue(written > 0, "manifest was written");

            AssertTrue(DiskBufferManifest.TryRead(path, out var parsed), "manifest parses");
            AssertEqual(DiskBufferFormat.FormatVersion, parsed.FormatVersion, "format version");
            AssertEqual("Android", parsed.Platform, "platform");
            AssertEqual("1.0.0-\"quoted\"\\slash", parsed.ApplicationVersion, "escaped application version");
            AssertEqual(1280u, parsed.ToVideoOptions().Width, "video width");
            AssertEqual(44100u, parsed.ToAudioOptions().SampleRate, "audio sample rate");
            AssertTrue(parsed.IsCompatibleWith("Android"), "compatible on the recording platform");
            AssertTrue(!parsed.IsCompatibleWith("IPhonePlayer"), "incompatible on another platform");
            AssertTrue(parsed.GetStartedAtUtc() != default, "start time parses");

            AssertTrue(!DiskBufferManifest.TryParse("{ \"videoOptions\": { \"width\": 1 } }", out _),
                "a nested document is rejected rather than partially accepted");
        }

        private static void SelectionStartsAtNearestKeyFrame(string directory)
        {
            _ = directory;

            var video = new EncodedFrameDescriptor[20];
            for (var i = 0; i < video.Length; i++)
                video[i] = new EncodedFrameDescriptor(i * 0.5,
                    i % 4 == 0 ? UniencSampleKind.Key : UniencSampleKind.Interpolated);

            var audio = new EncodedFrameDescriptor[20];
            for (var i = 0; i < audio.Length; i++)
                audio[i] = new EncodedFrameDescriptor(i * 0.5, UniencSampleKind.Key);

            // Latest video timestamp is 9.5; asking for 4 seconds should start near 5.5, whose nearest key frame is
            // index 12 at t=6.0.
            AssertTrue(EncodedFrameSelector.TrySelect(video, audio, 9.5, 4.0, out var videoStart, out var audioStart),
                "selection succeeds");
            AssertEqual(12, videoStart, "video start index");
            AssertEqual(UniencSampleKind.Key, video[videoStart].Kind, "video starts at a key frame");
            AssertTrue(audioStart >= 0, "audio start index is valid");

            // Without a key frame nothing can be exported.
            var noKey = new EncodedFrameDescriptor[3];
            for (var i = 0; i < noKey.Length; i++)
                noKey[i] = new EncodedFrameDescriptor(i, UniencSampleKind.Interpolated);
            AssertTrue(!EncodedFrameSelector.TrySelect(noKey, audio, 2.0, null, out _, out _),
                "selection fails without a key frame");

            // An empty buffer is not an error either.
            AssertTrue(!EncodedFrameSelector.TrySelect(Array.Empty<EncodedFrameDescriptor>(), audio, 0, null,
                out _, out _), "selection fails on an empty buffer");
        }

        private static void RunEndToEnd()
        {
            const string name = "end to end: encode, persist, recover, mux";

            if (!EndToEnd.IsSupported())
            {
                Console.WriteLine($"  [skip] {name} (native library unavailable on this platform)");
                return;
            }

            var directory = Path.Combine(Path.GetTempPath(),
                "instantreplay-diskbuffer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var result = EndToEnd.RunAsync(directory).GetAwaiter().GetResult();

                AssertTrue(result.Success, $"end-to-end run succeeded ({result.Error})");
                AssertTrue(result.VideoRecords > 10, $"video records were persisted ({result.VideoRecords})");
                AssertTrue(result.AudioRecords > 10, $"audio records were persisted ({result.AudioRecords})");
                AssertTrue(result.OutputBytes > 4096, $"recovered MP4 is non-trivial ({result.OutputBytes} bytes)");

                if (result.OutputPath != null && File.Exists(result.OutputPath))
                {
                    AssertTrue(IsMp4(result.OutputPath), "recovered file carries an MP4 signature");

                    var keep = Environment.GetEnvironmentVariable("INSTANTREPLAY_TEST_KEEP_OUTPUT");
                    if (!string.IsNullOrEmpty(keep)) File.Copy(result.OutputPath, keep, true);
                }

                Console.WriteLine($"         video={result.VideoRecords} audio={result.AudioRecords} " +
                                  $"metadata={result.MetadataRecords} mp4={result.OutputBytes} bytes");
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"  [ERROR] {name}: {ex}");
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (Exception)
                {
                    // not a failure
                }
            }

            Console.WriteLine($"  [done] {name}");
        }

        private static bool IsMp4(string path)
        {
            var header = new byte[12];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                if (stream.Read(header, 0, header.Length) < header.Length) return false;
            }

            return header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' &&
                   header[7] == (byte)'p';
        }

        // ---------------------------------------------------------------- helpers

        private static DiskBufferSegmentWriter CreateWriter(string directory)
        {
            return new DiskBufferSegmentWriter(directory, 64 * 1024 * 1024, 5.0, 8 * 1024 * 1024,
                DiskBufferSyncMode.OperatingSystem);
        }

        private static void Write(DiskBufferSegmentWriter writer, DiskBufferTrack track, UniencSampleKind kind,
            double timestamp, byte[] payload)
        {
            using var frame = EncodedFrame.CreateWithCopy(payload, timestamp, kind);
            writer.Write(track, frame);
        }

        private static byte[] Payload(int size, byte seed)
        {
            var payload = new byte[size];
            for (var i = 0; i < size; i++) payload[i] = (byte)(seed + i);
            return payload;
        }

        private static void Truncate(string path, long length)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
            stream.SetLength(length);
        }

        private static long DirectorySize(string directory)
        {
            return Directory.GetFiles(directory).Sum(file => new FileInfo(file).Length);
        }

        private static void Run(string name, Action<string> check)
        {
            var directory = Path.Combine(Path.GetTempPath(),
                "instantreplay-diskbuffer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var before = _failures;
            try
            {
                check(directory);
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"  [ERROR] {name}: {ex}");
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (Exception)
                {
                    // The check itself is what matters; a leftover temporary directory is not a failure.
                }
            }

            Console.WriteLine(_failures == before ? $"  [ok]   {name}" : $"  [FAIL] {name}");
        }

        private static void AssertTrue(bool condition, string what)
        {
            if (condition) return;
            _failures++;
            Console.WriteLine($"    assertion failed: {what}");
        }

        private static void AssertEqual<T>(T expected, T actual, string what)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual)) return;
            _failures++;
            Console.WriteLine($"    assertion failed: {what} (expected {expected}, got {actual})");
        }
    }
}
