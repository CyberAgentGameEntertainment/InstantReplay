// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Recovers encoded frame data from a previous <see cref="DiskEncodedFrameBuffer"/>
    ///     session that was not properly cleaned up (e.g. after a crash).
    /// </summary>
    public class DiskEncodedFrameBufferRecovery : IDisposable
    {
        private readonly string _storagePath;
        private readonly DiskBufferManifest _manifest;
        private readonly DiskEncodedFrameBuffer.FrameIndexEntry[] _videoEntries;
        private readonly DiskEncodedFrameBuffer.FrameIndexEntry[] _audioEntries;
        private bool _disposed;

        private DiskEncodedFrameBufferRecovery(
            string storagePath,
            DiskBufferManifest manifest,
            DiskEncodedFrameBuffer.FrameIndexEntry[] videoEntries,
            DiskEncodedFrameBuffer.FrameIndexEntry[] audioEntries)
        {
            _storagePath = storagePath;
            _manifest = manifest;
            _videoEntries = videoEntries;
            _audioEntries = audioEntries;
        }

        /// <summary>
        ///     Attempts to detect and load recoverable data from a previous disk buffer session.
        /// </summary>
        /// <param name="storagePath">Path to the disk buffer storage directory.</param>
        /// <param name="recovery">The recovery instance if data is recoverable; null otherwise.</param>
        /// <returns>True if recoverable data was found.</returns>
        public static bool TryGetRecoverable(string storagePath, out DiskEncodedFrameBufferRecovery recovery)
        {
            recovery = null;

            try
            {
                var manifestPath = Path.Combine(storagePath, "manifest.json");
                if (!File.Exists(manifestPath))
                    return false;

                var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = DiskBufferManifest.FromJson(manifestJson);
                if (manifest == null || manifest.Version != 1)
                    return false;

                var videoEntries = LoadValidIndexEntries(Path.Combine(storagePath, "video.idx"));
                var audioEntries = LoadValidIndexEntries(Path.Combine(storagePath, "audio.idx"));

                // Need at least one video keyframe
                var hasKeyframe = false;
                for (var i = 0; i < videoEntries.Length; i++)
                {
                    if (videoEntries[i].Kind == UniencSampleKind.Key)
                    {
                        hasKeyframe = true;
                        break;
                    }
                }

                if (!hasKeyframe)
                    return false;

                recovery = new DiskEncodedFrameBufferRecovery(storagePath, manifest, videoEntries, audioEntries);
                return true;
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
                return false;
            }
        }

        /// <summary>
        ///     Exports the recovered data to an MP4 file.
        /// </summary>
        /// <param name="durationSeconds">Maximum duration to export in seconds. Null exports all available data.</param>
        /// <param name="outputPath">Output file path. If null, a default path will be generated.</param>
        /// <returns>Path to the exported video file.</returns>
        public async Task<string> ExportAsync(double? durationSeconds = null, string outputPath = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiskEncodedFrameBufferRecovery));

            var videoOptions = _manifest.VideoOptions.ToEncoderOptions();
            var audioOptions = _manifest.AudioOptions.ToEncoderOptions();

            if (string.IsNullOrEmpty(outputPath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"InstantReplay_Recovered_{timestamp}.mp4";
                outputPath = Path.Combine(Application.temporaryCachePath, fileName);
            }

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            // Select frames using the same keyframe search logic
            SelectFrames(durationSeconds, out var selectedVideo, out var selectedAudio);

            using var encodingSystem = new EncodingSystem(videoOptions, audioOptions);
            using var muxer = encodingSystem.CreateMuxer(outputPath);

            var videoTask = Task.Run(async () =>
            {
                var datPath = Path.Combine(_storagePath, "video.dat");
                Exception exception = null;
                using var stream = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);

                for (var i = 0; i < selectedVideo.Length; i++)
                {
                    var entry = selectedVideo[i];
                    var data = new byte[entry.DataLength];
                    stream.Position = entry.DataOffset;
                    var totalRead = 0;
                    while (totalRead < entry.DataLength)
                    {
                        var read = stream.Read(data, totalRead, entry.DataLength - totalRead);
                        if (read == 0) throw new EndOfStreamException("Unexpected end of video data file.");
                        totalRead += read;
                    }

                    using var frame = EncodedFrame.CreateWithCopy(data, entry.Timestamp, entry.Kind);
                    try
                    {
                        if (exception == null)
                            await muxer.PushVideoDataAsync(frame);
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                }

                if (exception != null)
                    throw exception;

                await muxer.FinishVideoAsync();
            });

            var audioTask = Task.Run(async () =>
            {
                var datPath = Path.Combine(_storagePath, "audio.dat");
                Exception exception = null;

                if (selectedAudio.Length == 0)
                {
                    await muxer.FinishAudioAsync();
                    return;
                }

                using var stream = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);

                for (var i = 0; i < selectedAudio.Length; i++)
                {
                    var entry = selectedAudio[i];
                    var data = new byte[entry.DataLength];
                    stream.Position = entry.DataOffset;
                    var totalRead = 0;
                    while (totalRead < entry.DataLength)
                    {
                        var read = stream.Read(data, totalRead, entry.DataLength - totalRead);
                        if (read == 0) throw new EndOfStreamException("Unexpected end of audio data file.");
                        totalRead += read;
                    }

                    using var frame = EncodedFrame.CreateWithCopy(data, entry.Timestamp, entry.Kind);
                    try
                    {
                        if (exception == null)
                            await muxer.PushAudioDataAsync(frame);
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                }

                if (exception != null)
                    throw exception;

                await muxer.FinishAudioAsync();
            });

            await Task.WhenAll(videoTask, audioTask).ConfigureAwait(false);
            await muxer.CompleteAsync().ConfigureAwait(false);

            return outputPath;
        }

        /// <summary>
        ///     Deletes the storage directory and all recovery data.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (Directory.Exists(_storagePath))
                    Directory.Delete(_storagePath, true);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }
        }

        private void SelectFrames(double? durationSeconds,
            out DiskEncodedFrameBuffer.FrameIndexEntry[] videoOut,
            out DiskEncodedFrameBuffer.FrameIndexEntry[] audioOut)
        {
            if (_videoEntries.Length == 0)
            {
                videoOut = Array.Empty<DiskEncodedFrameBuffer.FrameIndexEntry>();
                audioOut = Array.Empty<DiskEncodedFrameBuffer.FrameIndexEntry>();
                return;
            }

            var latest = _videoEntries[_videoEntries.Length - 1].Timestamp;
            var argMinTimespan = -1;

            if (durationSeconds is { } dur)
            {
                var expectedStartTime = latest - dur;
                var minTimespan = double.MaxValue;
                for (var i = 0; i < _videoEntries.Length; i++)
                {
                    if (_videoEntries[i].Kind != UniencSampleKind.Key) continue;
                    var ts = Math.Abs(_videoEntries[i].Timestamp - expectedStartTime);
                    if (ts >= minTimespan) continue;
                    minTimespan = ts;
                    argMinTimespan = i;
                }
            }
            else
            {
                for (var i = 0; i < _videoEntries.Length; i++)
                {
                    if (_videoEntries[i].Kind != UniencSampleKind.Key) continue;
                    argMinTimespan = i;
                    break;
                }
            }

            if (argMinTimespan == -1)
            {
                videoOut = Array.Empty<DiskEncodedFrameBuffer.FrameIndexEntry>();
                audioOut = Array.Empty<DiskEncodedFrameBuffer.FrameIndexEntry>();
                return;
            }

            // Select video frames from keyframe onwards
            var videoCount = _videoEntries.Length - argMinTimespan;
            videoOut = new DiskEncodedFrameBuffer.FrameIndexEntry[videoCount];
            Array.Copy(_videoEntries, argMinTimespan, videoOut, 0, videoCount);

            // Find matching audio start
            if (_audioEntries.Length == 0)
            {
                audioOut = Array.Empty<DiskEncodedFrameBuffer.FrameIndexEntry>();
            }
            else
            {
                var actualDuration = latest - _videoEntries[argMinTimespan].Timestamp;
                var expectedAudioStartTime = _audioEntries[_audioEntries.Length - 1].Timestamp - actualDuration;

                var minAudioTimespan = double.MaxValue;
                var argMinAudioTimespan = 0;
                for (var i = 0; i < _audioEntries.Length; i++)
                {
                    var ts = Math.Abs(_audioEntries[i].Timestamp - expectedAudioStartTime);
                    if (ts >= minAudioTimespan) continue;
                    minAudioTimespan = ts;
                    argMinAudioTimespan = i;
                }

                var audioCount = _audioEntries.Length - argMinAudioTimespan;
                audioOut = new DiskEncodedFrameBuffer.FrameIndexEntry[audioCount];
                Array.Copy(_audioEntries, argMinAudioTimespan, audioOut, 0, audioCount);
            }

            // Adjust timestamps so they start from 0
            if (videoOut.Length > 0)
            {
                var startTime = videoOut[0].Timestamp;
                for (var i = 0; i < videoOut.Length; i++)
                {
                    var e = videoOut[i];
                    videoOut[i] = new DiskEncodedFrameBuffer.FrameIndexEntry(
                        e.DataOffset, e.DataLength, e.Timestamp - startTime, e.Kind);
                }
            }

            if (audioOut.Length > 0)
            {
                var startTime = audioOut[0].Timestamp;
                for (var i = 0; i < audioOut.Length; i++)
                {
                    var e = audioOut[i];
                    audioOut[i] = new DiskEncodedFrameBuffer.FrameIndexEntry(
                        e.DataOffset, e.DataLength, e.Timestamp - startTime, e.Kind);
                }
            }
        }

        private static DiskEncodedFrameBuffer.FrameIndexEntry[] LoadValidIndexEntries(string indexPath)
        {
            if (!File.Exists(indexPath))
                return Array.Empty<DiskEncodedFrameBuffer.FrameIndexEntry>();

            var entries = new List<DiskEncodedFrameBuffer.FrameIndexEntry>();
            var buffer = new byte[DiskEncodedFrameBuffer.IndexEntrySize];

            using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            while (stream.Position + DiskEncodedFrameBuffer.IndexEntrySize <= stream.Length)
            {
                var totalRead = 0;
                while (totalRead < DiskEncodedFrameBuffer.IndexEntrySize)
                {
                    var read = stream.Read(buffer, totalRead, DiskEncodedFrameBuffer.IndexEntrySize - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }

                if (totalRead < DiskEncodedFrameBuffer.IndexEntrySize)
                    break;

                var valid = BitConverter.ToInt32(buffer, 21);
                if (valid != unchecked((int)0xCAFE1234))
                    continue;

                var offset = BitConverter.ToInt64(buffer, 0);
                var length = BitConverter.ToInt32(buffer, 8);
                var timestamp = BitConverter.ToDouble(buffer, 12);
                var kind = (UniencSampleKind)buffer[20];

                entries.Add(new DiskEncodedFrameBuffer.FrameIndexEntry(offset, length, timestamp, kind));
            }

            return entries.ToArray();
        }
    }
}
