// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Recovers encoded frames from a <see cref="DiskEncodedFrameBuffer" /> session that was not cleaned up, which
    ///     happens when the process terminated abnormally.
    /// </summary>
    /// <remarks>
    ///     A session that is disposed normally removes its own directory, so every directory that remains denotes an
    ///     abnormal termination. Recovery never deletes anything on its own: <see cref="Delete" /> must be called
    ///     explicitly, so that a failed export can be retried and the raw directory can still be retrieved from the device.
    /// </remarks>
    public sealed class DiskEncodedFrameBufferRecovery
    {
        private readonly DiskBufferManifest _manifest;

        private DiskEncodedFrameBufferRecovery(string storagePath, DiskBufferManifest manifest)
        {
            StoragePath = storagePath;
            _manifest = manifest;
        }

        /// <summary>
        ///     Directory holding the recovered session.
        /// </summary>
        public string StoragePath { get; }

        /// <summary>
        ///     When the recorded session started, in UTC.
        /// </summary>
        public DateTime StartedAtUtc => _manifest.GetStartedAtUtc();

        /// <summary>
        ///     Platform the session was recorded on, as reported by <c>Application.platform</c>.
        /// </summary>
        public string Platform => _manifest.Platform;

        /// <summary>
        ///     Application version the session was recorded with.
        /// </summary>
        public string ApplicationVersion => _manifest.ApplicationVersion;

        /// <summary>
        ///     Whether the running build can read this session back. Matching the buffer format and the platform is
        ///     necessary but not sufficient, because the payloads are serialized by the native library and its schema may
        ///     change between package versions. A mismatch of that kind surfaces as a failure from
        ///     <see cref="ExportAsync" />.
        /// </summary>
        public bool IsCompatible => _manifest.IsCompatibleWith(Application.platform.ToString());

        /// <summary>
        ///     Total size of the recovered session directory in bytes.
        /// </summary>
        public long SizeBytes
        {
            get
            {
                try
                {
                    if (!Directory.Exists(StoragePath)) return 0;

                    long total = 0;
                    foreach (var file in Directory.GetFiles(StoragePath))
                        total += new FileInfo(file).Length;
                    return total;
                }
                catch (Exception ex)
                {
                    ILogger.LogExceptionCore(ex);
                    return 0;
                }
            }
        }

        /// <summary>
        ///     Attempts to load a single session directory.
        /// </summary>
        /// <param name="storagePath">Directory of one session, containing the manifest.</param>
        /// <param name="recovery">The recovery instance when the session is readable; null otherwise.</param>
        /// <returns>True when a session with at least one video key frame was found.</returns>
        public static bool TryGetRecoverable(string storagePath, out DiskEncodedFrameBufferRecovery recovery)
        {
            recovery = null;

            try
            {
                if (string.IsNullOrEmpty(storagePath) || !Directory.Exists(storagePath)) return false;

                var manifestPath = Path.Combine(storagePath, DiskBufferFormat.ManifestFileName);
                if (!DiskBufferManifest.TryRead(manifestPath, out var manifest, ILogger.LogExceptionCore))
                    return false;

                if (manifest.FormatVersion != DiskBufferFormat.FormatVersion) return false;

                var scan = DiskBufferSegmentReader.Scan(storagePath, ILogger.LogExceptionCore);

                var hasKeyframe = false;
                foreach (var record in scan.VideoSamples)
                    if (record.Kind == UniencSampleKind.Key)
                    {
                        hasKeyframe = true;
                        break;
                    }

                if (!hasKeyframe) return false;

                recovery = new DiskEncodedFrameBufferRecovery(storagePath, manifest);
                return true;
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
                return false;
            }
        }

        /// <summary>
        ///     Enumerates every recoverable session below the given root directory. Pass null to search the directory the
        ///     recorder uses by default. Several sessions may be present when the application has terminated abnormally
        ///     more than once; the caller decides which to export and which to delete.
        /// </summary>
        public static IReadOnlyList<DiskEncodedFrameBufferRecovery> FindRecoverable(string rootDirectory = null)
        {
            var result = new List<DiskEncodedFrameBufferRecovery>();

            try
            {
                var root = string.IsNullOrEmpty(rootDirectory)
                    ? DiskBufferOptions.GetDefaultDirectory()
                    : rootDirectory;

                if (!Directory.Exists(root)) return result;

                var directories = Directory.GetDirectories(root);
                Array.Sort(directories, StringComparer.Ordinal);

                foreach (var directory in directories)
                    if (TryGetRecoverable(directory, out var recovery))
                        result.Add(recovery);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }

            return result;
        }

        /// <summary>
        ///     Exports the recovered data to an MP4 file. The session directory is left in place; call <see cref="Delete" />
        ///     when it is no longer needed.
        /// </summary>
        /// <param name="durationSeconds">Duration to export in seconds. Null exports from the earliest key frame.</param>
        /// <param name="outputPath">Output file path. A default path is generated when null.</param>
        /// <returns>Path to the exported video file.</returns>
        public async ValueTask<string> ExportAsync(double? durationSeconds = null, string outputPath = null)
        {
            if (!IsCompatible)
                ILogger.LogWarningCore(
                    $"Recovering a disk buffer recorded on platform '{_manifest.Platform}' with application version " +
                    $"'{_manifest.ApplicationVersion}'. The payload format is defined by the native library, so the " +
                    "export may fail if it differs from the running build.");

            if (string.IsNullOrEmpty(outputPath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                outputPath = Path.Combine(Application.temporaryCachePath,
                    $"InstantReplay_Recovered_{timestamp}.mp4");
            }

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var scan = DiskBufferSegmentReader.Scan(StoragePath, ILogger.LogExceptionCore);
            var selection = DiskBufferSegmentReader.BuildSelection(scan, durationSeconds, ILogger.LogExceptionCore);

            if (selection.VideoFrames.Length == 0)
                throw new InvalidOperationException("The disk buffer contains no muxable video frames.");

            using var encodingSystem = new EncodingSystem(_manifest.ToVideoOptions(), _manifest.ToAudioOptions());
            using var muxer = encodingSystem.CreateMuxer(outputPath);

            await EncodedFrameMuxer.MuxAsync(muxer, selection.VideoFrames, selection.AudioFrames)
                .ConfigureAwait(false);

            return outputPath;
        }

        /// <summary>
        ///     Deletes the recovered session directory. Recovery never does this implicitly, because a session left behind
        ///     by a crash is the only copy of the footage that preceded it.
        /// </summary>
        public void Delete()
        {
            try
            {
                if (Directory.Exists(StoragePath)) Directory.Delete(StoragePath, true);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }
        }
    }
}
