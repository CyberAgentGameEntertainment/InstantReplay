// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Encoded frame buffer backed by disk storage.
    ///     Frame payloads live in segment files that rotate at video key frames; only the write queue holds them in
    ///     memory. A session left behind by an abnormal termination is recoverable through
    ///     <see cref="DiskEncodedFrameBufferRecovery" />.
    /// </summary>
    internal sealed class DiskEncodedFrameBuffer : IEncodedFrameBuffer
    {
        private static int _sessionCounter;

        private readonly object _lock = new();
        private readonly long _maxPendingWriteBytes;
        private readonly Queue<PendingWrite> _pending = new();
        private readonly bool _retainOnDispose;
        private readonly string _sessionDirectory;
        private readonly Thread _worker;
        private readonly DiskBufferSegmentWriter _writer;

        private bool _completed;
        private bool _disposed;
        private long _droppedFrameCount;
        private long _pendingBytes;
        private bool _warnedAboutDrops;

        public DiskEncodedFrameBuffer(in DiskBufferOptions options, in VideoEncoderOptions videoOptions,
            in AudioEncoderOptions audioOptions)
        {
            var validated = options;
            validated.Validate();

            _retainOnDispose = validated.RetainOnDispose;
            _maxPendingWriteBytes = validated.MaxPendingWriteBytes;

            var root = validated.ResolveDirectory();
            _sessionDirectory = Path.Combine(root, CreateSessionId());
            Directory.CreateDirectory(_sessionDirectory);

            _writer = new DiskBufferSegmentWriter(_sessionDirectory, validated.MaxDiskUsageBytes,
                validated.SegmentDuration, validated.MaxSegmentBytes, validated.SyncMode, ILogger.LogExceptionCore);

            var manifest = DiskBufferManifest.Create(videoOptions, audioOptions, Application.platform.ToString(),
                Application.unityVersion, Application.version);
            _writer.SetManifestBytes(manifest.Write(Path.Combine(_sessionDirectory,
                DiskBufferFormat.ManifestFileName)));

            _worker = new Thread(RunWorker)
            {
                Name = "InstantReplay.DiskBuffer",
                IsBackground = true
            };
            _worker.Start();
        }

        /// <summary>
        ///     Directory this session writes to.
        /// </summary>
        public string SessionDirectory => _sessionDirectory;

        public bool TryAddVideoFrame(EncodedFrame frame)
        {
            return TryEnqueue(DiskBufferTrack.Video, frame);
        }

        public bool TryAddAudioFrame(EncodedFrame frame)
        {
            return TryEnqueue(DiskBufferTrack.Audio, frame);
        }

        public ValueTask<EncodedFrameSelection> GetFramesForDurationAsync(double? durationSeconds)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiskEncodedFrameBuffer));

            // The worker owns the files, so it must have drained and closed them before they can be read back.
            Quiesce();

            var scan = DiskBufferSegmentReader.Scan(_sessionDirectory, ILogger.LogExceptionCore);
            var selection = DiskBufferSegmentReader.BuildSelection(scan, durationSeconds, ILogger.LogExceptionCore);
            return new ValueTask<EncodedFrameSelection>(selection);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            Quiesce();

            if (!_retainOnDispose) DeleteSessionDirectory();
        }

        /// <summary>
        ///     Deletes the session directory. Called after a successful export, and when a session that was disposed
        ///     normally is not configured to be retained.
        /// </summary>
        public void CleanupStorage()
        {
            DeleteSessionDirectory();
        }

        private bool TryEnqueue(DiskBufferTrack track, EncodedFrame frame)
        {
            var length = frame.Data.Length;

            lock (_lock)
            {
                if (_disposed || _completed) return false;

                if (_pendingBytes + length > _maxPendingWriteBytes)
                {
                    // Storage cannot keep up. Dropping here rather than blocking keeps the encoder from stalling, which
                    // is the same trade-off DroppingChannelInput makes for raw frames.
                    _droppedFrameCount++;
                    if (!_warnedAboutDrops)
                    {
                        _warnedAboutDrops = true;
                        ILogger.LogWarningCore(
                            "Dropped an encoded frame because the disk buffer write queue is full. " +
                            "Storage is not keeping up with the encoder; the exported video may show artefacts.");
                    }

                    return false;
                }

                _pending.Enqueue(new PendingWrite(track, frame));
                _pendingBytes += length;
                Monitor.Pulse(_lock);
                return true;
            }
        }

        private void RunWorker()
        {
            while (true)
            {
                PendingWrite item;

                lock (_lock)
                {
                    while (_pending.Count == 0)
                    {
                        if (_completed) return;
                        Monitor.Wait(_lock);
                    }

                    item = _pending.Dequeue();
                    _pendingBytes -= item.Frame.Data.Length;
                }

                try
                {
                    using (item.Frame)
                    {
                        _writer.Write(item.Track, item.Frame);
                    }
                }
                catch (Exception ex)
                {
                    ILogger.LogExceptionCore(ex);
                }

                bool idle;
                lock (_lock)
                {
                    idle = _pending.Count == 0;
                }

                // Handing the batch to the operating system is what makes it survive a process crash. It costs a write
                // syscall and no device flush, so it does not add wear to flash memory.
                if (idle) _writer.FlushToOperatingSystem();
            }
        }

        /// <summary>
        ///     Stops accepting frames, drains everything already accepted, and closes the files.
        /// </summary>
        private void Quiesce()
        {
            lock (_lock)
            {
                if (_completed) return;
                _completed = true;
                Monitor.PulseAll(_lock);
            }

            try
            {
                _worker.Join();
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }

            lock (_lock)
            {
                // Anything still queued was never written; release it so no pooled array is leaked.
                while (_pending.Count > 0)
                    try
                    {
                        _pending.Dequeue().Frame.Dispose();
                    }
                    catch (Exception ex)
                    {
                        ILogger.LogExceptionCore(ex);
                    }

                _pendingBytes = 0;
            }

            try
            {
                _writer.Dispose();
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }

            var dropped = _droppedFrameCount + _writer.DroppedRecordCount;
            if (dropped > 0)
                ILogger.LogWarningCore(
                    $"The disk buffer dropped {dropped} encoded frame(s) during this session.");
        }

        private void DeleteSessionDirectory()
        {
            try
            {
                if (Directory.Exists(_sessionDirectory)) Directory.Delete(_sessionDirectory, true);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }
        }

        private static string CreateSessionId()
        {
            var counter = Interlocked.Increment(ref _sessionCounter);
            return string.Format(CultureInfo.InvariantCulture, "{0:yyyyMMdd_HHmmssfff}_{1:D4}", DateTime.Now, counter);
        }

        private readonly struct PendingWrite
        {
            public readonly DiskBufferTrack Track;
            public readonly EncodedFrame Frame;

            public PendingWrite(DiskBufferTrack track, EncodedFrame frame)
            {
                Track = track;
                Frame = frame;
            }
        }
    }
}
