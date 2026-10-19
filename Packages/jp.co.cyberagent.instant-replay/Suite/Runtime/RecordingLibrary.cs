// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     How a recording was produced.
    /// </summary>
    public enum RecordingSource
    {
        /// <summary>Saved from the continuous buffer.</summary>
        Replay,

        /// <summary>Recorded from start to stop without a bound.</summary>
        Unbounded,

        /// <summary>Exported from a disk buffer left behind by an abnormal termination.</summary>
        Recovered
    }

    /// <summary>
    ///     One recording in a <see cref="RecordingLibrary" />.
    /// </summary>
    public sealed class RecordingEntry
    {
        internal RecordingEntry(string id, string filePath, DateTime createdAtUtc, double durationSeconds,
            long sizeBytes, RecordingSource source, IReadOnlyDictionary<string, string> metadata, string thumbnailPath)
        {
            Id = id;
            FilePath = filePath;
            CreatedAtUtc = createdAtUtc;
            DurationSeconds = durationSeconds;
            SizeBytes = sizeBytes;
            Source = source;
            Metadata = metadata;
            ThumbnailPath = thumbnailPath;
        }

        /// <summary>Identifier unique within the library. It is the file name without extension.</summary>
        public string Id { get; }

        /// <summary>Absolute path of the video file.</summary>
        public string FilePath { get; }

        public DateTime CreatedAtUtc { get; }

        /// <summary>Approximate duration in seconds. Zero when unknown.</summary>
        public double DurationSeconds { get; }

        public long SizeBytes { get; }
        public RecordingSource Source { get; }

        /// <summary>Key-value pairs attached when the recording was saved. Never null.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; }

        /// <summary>Absolute path of the thumbnail image, or null when there is none.</summary>
        public string ThumbnailPath { get; }
    }

    /// <summary>
    ///     Recordings saved in the output directory, each described by a sidecar JSON file next to the video file.
    /// </summary>
    public sealed class RecordingLibrary
    {
        private const string VideoExtension = ".mp4";
        private const string SidecarExtension = ".json";
        private const string ThumbnailExtension = ".png";

        private readonly int _maxRecordings;
        private readonly long _maxTotalBytes;

        // Files being written are not recordings yet, so they are hidden from the listing and from retention.
        private readonly HashSet<string> _pendingPaths = new(StringComparer.Ordinal);

        internal RecordingLibrary(string directoryPath, int maxRecordings = 0, long maxTotalBytes = 0)
        {
            DirectoryPath = directoryPath;
            _maxRecordings = maxRecordings;
            _maxTotalBytes = maxTotalBytes;
        }

        /// <summary>Absolute path of the directory that holds the recordings.</summary>
        public string DirectoryPath { get; }

        /// <summary>Raised on the main thread when a recording is added or deleted.</summary>
        public event Action Changed;

        /// <summary>
        ///     Lists the recordings, newest first. Video files without a sidecar are listed with the information that can
        ///     be read from the file system.
        /// </summary>
        public IReadOnlyList<RecordingEntry> GetEntries()
        {
            var entries = new List<RecordingEntry>();
            if (!Directory.Exists(DirectoryPath)) return entries;

            foreach (var videoPath in Directory.GetFiles(DirectoryPath, "*" + VideoExtension))
            {
                if (_pendingPaths.Contains(videoPath)) continue;
                try
                {
                    entries.Add(ReadEntry(videoPath));
                }
                catch (Exception ex)
                {
                    ILogger.LogExceptionCore(ex);
                }
            }

            entries.Sort((a, b) => b.CreatedAtUtc.CompareTo(a.CreatedAtUtc));
            return entries;
        }

        /// <summary>
        ///     Deletes the video file of the entry together with its sidecar and thumbnail.
        /// </summary>
        public void Delete(RecordingEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            DeleteFiles(entry);
            Changed?.Invoke();
        }

        /// <summary>
        ///     Stores a PNG image as the thumbnail of the entry, replacing the current one.
        /// </summary>
        /// <param name="entry">The recording.</param>
        /// <param name="png">PNG-encoded image, for example from <c>ImageConversion.EncodeToPNG</c>.</param>
        /// <returns>The entry with the new <see cref="RecordingEntry.ThumbnailPath" />.</returns>
        public RecordingEntry SetThumbnail(RecordingEntry entry, byte[] png)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (png == null || png.Length == 0) throw new ArgumentException("The image is empty.", nameof(png));
            if (!File.Exists(entry.FilePath))
                throw new FileNotFoundException("The recording no longer exists.", entry.FilePath);

            var thumbnailName = WriteThumbnail(entry.FilePath, png);
            WriteSidecar(entry.FilePath, entry.CreatedAtUtc, entry.DurationSeconds, entry.Source, entry.Metadata,
                thumbnailName);

            var updated = ReadEntry(entry.FilePath);
            Changed?.Invoke();
            return updated;
        }

        /// <summary>
        ///     Deletes several entries in the same way as <see cref="Delete(RecordingEntry)" />, raising
        ///     <see cref="Changed" /> once. Every entry is attempted even when some fail.
        /// </summary>
        /// <exception cref="AggregateException">One or more entries could not be deleted.</exception>
        public void Delete(IEnumerable<RecordingEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            List<Exception> errors = null;
            var deleted = false;
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                try
                {
                    DeleteFiles(entry);
                    deleted = true;
                }
                catch (Exception ex)
                {
                    (errors ??= new List<Exception>()).Add(ex);
                }
            }

            if (deleted || errors != null) Changed?.Invoke();
            if (errors != null) throw new AggregateException("Some recordings could not be deleted.", errors);
        }

        private static void DeleteFiles(RecordingEntry entry)
        {
            DeleteIfExists(entry.FilePath);
            DeleteIfExists(GetSidecarPath(entry.FilePath));
            if (!string.IsNullOrEmpty(entry.ThumbnailPath)) DeleteIfExists(entry.ThumbnailPath);
        }

        /// <summary>
        ///     Exports a disk buffer left behind by an abnormal termination into the library as a
        ///     <see cref="RecordingSource.Recovered" /> recording. The recovered session is kept; call
        ///     <see cref="DiskEncodedFrameBufferRecovery.Delete" /> once it is no longer needed.
        /// </summary>
        /// <remarks>Call it on the main thread. <see cref="Changed" /> is raised on the main thread when it completes.</remarks>
        public async ValueTask<RecordingEntry> ImportRecoveredAsync(DiskEncodedFrameBufferRecovery recovery,
            RecoveryImportRequest request = null)
        {
            if (recovery == null) throw new ArgumentNullException(nameof(recovery));

            var path = CreateOutputPath(RecordingSource.Recovered);
            var completed = false;
            try
            {
                await recovery.ExportAsync(request?.DurationSeconds, path);
                completed = true;
                return Add(path, RecordingSource.Recovered, recovery.StartedAtUtc, 0, request?.Metadata);
            }
            finally
            {
                if (!completed) Abandon(path);
            }
        }

        internal string CreateOutputPath(RecordingSource source)
        {
            Directory.CreateDirectory(DirectoryPath);
            var prefix = source switch
            {
                RecordingSource.Unbounded => "Recording",
                RecordingSource.Recovered => "Recovered",
                _ => "Replay"
            };
            var stem = $"{prefix}_{DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}";
            var path = Path.Combine(DirectoryPath, stem + VideoExtension);
            for (var i = 1; File.Exists(path); i++)
                path = Path.Combine(DirectoryPath, $"{stem}_{i}{VideoExtension}");
            _pendingPaths.Add(path);
            return path;
        }

        /// <summary>
        ///     Deletes a file returned by <see cref="CreateOutputPath" /> that could not be completed.
        /// </summary>
        internal void Abandon(string filePath)
        {
            _pendingPaths.Remove(filePath);
            try
            {
                DeleteIfExists(filePath);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }
        }

        internal RecordingEntry Add(string filePath, RecordingSource source, DateTime createdAtUtc,
            double durationSeconds, IReadOnlyDictionary<string, string> metadata, byte[] thumbnailPng = null)
        {
            _pendingPaths.Remove(filePath);

            try
            {
                var thumbnailName = thumbnailPng is { Length: > 0 } ? WriteThumbnail(filePath, thumbnailPng) : null;
                WriteSidecar(filePath, createdAtUtc, durationSeconds, source, metadata, thumbnailName);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }

            var entry = ReadEntry(filePath);
            ApplyRetention(entry);
            Changed?.Invoke();
            return entry;
        }

        // Deletes the oldest recordings beyond the configured count or total size. The recording just added is kept even
        // when it alone exceeds the size, because deleting what the user has just saved would be surprising.
        private void ApplyRetention(RecordingEntry added)
        {
            if (_maxRecordings <= 0 && _maxTotalBytes <= 0) return;

            var entries = GetEntries();
            var count = 0;
            long total = 0;
            foreach (var entry in entries)
            {
                var keep = entry.FilePath == added.FilePath ||
                           ((_maxRecordings <= 0 || count < _maxRecordings) &&
                            (_maxTotalBytes <= 0 || total + entry.SizeBytes <= _maxTotalBytes));
                if (keep)
                {
                    count++;
                    total += entry.SizeBytes;
                    continue;
                }

                try
                {
                    DeleteFiles(entry);
                }
                catch (Exception ex)
                {
                    ILogger.LogExceptionCore(ex);
                }
            }
        }

        private static RecordingEntry ReadEntry(string videoPath)
        {
            var info = new FileInfo(videoPath);
            var id = Path.GetFileNameWithoutExtension(videoPath);
            var createdAtUtc = info.CreationTimeUtc;
            var duration = 0.0;
            var source = RecordingSource.Replay;
            var metadata = new Dictionary<string, string>();
            string thumbnailPath = null;

            var sidecarPath = GetSidecarPath(videoPath);
            if (File.Exists(sidecarPath))
            {
                var sidecar = JsonUtility.FromJson<Sidecar>(File.ReadAllText(sidecarPath));
                if (sidecar is { version: Sidecar.CurrentVersion })
                {
                    if (sidecar.createdAtUtcTicks > 0)
                        createdAtUtc = new DateTime(sidecar.createdAtUtcTicks, DateTimeKind.Utc);
                    duration = sidecar.durationSeconds;
                    if (Enum.TryParse(sidecar.source, out RecordingSource parsed)) source = parsed;
                    foreach (var pair in sidecar.metadata)
                        if (!string.IsNullOrEmpty(pair.key))
                            metadata[pair.key] = pair.value;
                    if (!string.IsNullOrEmpty(sidecar.thumbnail))
                    {
                        var candidate = Path.Combine(Path.GetDirectoryName(videoPath) ?? "", sidecar.thumbnail);
                        if (File.Exists(candidate)) thumbnailPath = candidate;
                    }
                }
            }

            return new RecordingEntry(id, videoPath, createdAtUtc, duration, info.Exists ? info.Length : 0, source,
                metadata, thumbnailPath);
        }

        private static void WriteSidecar(string videoPath, DateTime createdAtUtc, double durationSeconds,
            RecordingSource source, IReadOnlyDictionary<string, string> metadata, string thumbnailName)
        {
            var sidecar = new Sidecar
            {
                version = Sidecar.CurrentVersion,
                createdAtUtcTicks = createdAtUtc.Ticks,
                durationSeconds = durationSeconds,
                source = source.ToString(),
                thumbnail = thumbnailName
            };
            if (metadata != null)
                foreach (var pair in metadata)
                    sidecar.metadata.Add(new MetadataPair { key = pair.Key, value = pair.Value });

            File.WriteAllText(GetSidecarPath(videoPath), JsonUtility.ToJson(sidecar));
        }

        // Returns the file name of the thumbnail relative to the directory of the video, as stored in the sidecar.
        private static string WriteThumbnail(string videoPath, byte[] png)
        {
            var path = Path.ChangeExtension(videoPath, ThumbnailExtension);
            File.WriteAllBytes(path, png);
            return Path.GetFileName(path);
        }

        private static string GetSidecarPath(string videoPath)
        {
            return Path.ChangeExtension(videoPath, SidecarExtension);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        [Serializable]
        private sealed class Sidecar
        {
            public const int CurrentVersion = 1;

            public int version;
            public long createdAtUtcTicks;
            public double durationSeconds;
            public string source;
            public string thumbnail;
            public List<MetadataPair> metadata = new();
        }

        [Serializable]
        private sealed class MetadataPair
        {
            public string key;
            public string value;
        }
    }
}
