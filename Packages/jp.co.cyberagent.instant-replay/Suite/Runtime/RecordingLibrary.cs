// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        internal RecordingLibrary(string directoryPath)
        {
            DirectoryPath = directoryPath;
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
                try
                {
                    entries.Add(ReadEntry(videoPath));
                }
                catch (Exception ex)
                {
                    ILogger.LogExceptionCore(ex);
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
            DeleteIfExists(entry.FilePath);
            DeleteIfExists(GetSidecarPath(entry.FilePath));
            if (!string.IsNullOrEmpty(entry.ThumbnailPath)) DeleteIfExists(entry.ThumbnailPath);
            Changed?.Invoke();
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
            return path;
        }

        internal RecordingEntry Add(string filePath, RecordingSource source, DateTime createdAtUtc,
            double durationSeconds, IReadOnlyDictionary<string, string> metadata)
        {
            var sidecar = new Sidecar
            {
                version = Sidecar.CurrentVersion,
                createdAtUtcTicks = createdAtUtc.Ticks,
                durationSeconds = durationSeconds,
                source = source.ToString()
            };
            if (metadata != null)
                foreach (var pair in metadata)
                    sidecar.metadata.Add(new MetadataPair { key = pair.Key, value = pair.Value });

            try
            {
                File.WriteAllText(GetSidecarPath(filePath), JsonUtility.ToJson(sidecar));
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }

            var entry = ReadEntry(filePath);
            Changed?.Invoke();
            return entry;
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
