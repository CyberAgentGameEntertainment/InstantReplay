// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
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
        private readonly Channel<PendingWrite> _pending;
        private readonly bool _retainOnDispose;
        private readonly string _sessionDirectory;
        private readonly Task _worker;
        private readonly DiskBufferSegmentWriter _writer;

        private bool _disposed;
        private long _droppedFrameCount;
        private long _pendingBytes;
        private Task _quiesceTask;
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

            // Synchronous continuations stay disabled so that file I/O never runs inline on the encoder thread that
            // enqueues a frame.
            _pending = Channel.CreateUnbounded<PendingWrite>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _worker = Task.Run(() => RunWorkerAsync(_pending.Reader));
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

        public async ValueTask<EncodedFrameSelection> GetFramesForDurationAsync(double? durationSeconds)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiskEncodedFrameBuffer));

            // The worker owns the files, so it must have drained and closed them before they can be read back.
            await QuiesceAsync().ConfigureAwait(false);

            var scan = DiskBufferSegmentReader.Scan(_sessionDirectory, ILogger.LogExceptionCore);
            return DiskBufferSegmentReader.BuildSelection(scan, durationSeconds, ILogger.LogExceptionCore);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            // Dispose is synchronous, so it blocks until the files are closed. The worker never resumes on the calling
            // thread's synchronization context, so blocking here cannot deadlock it.
            QuiesceAsync().GetAwaiter().GetResult();

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
                if (_disposed || _quiesceTask != null) return false;

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

                // The writer is completed only while holding the lock, so this cannot fail.
                _pending.Writer.TryWrite(new PendingWrite(track, frame));
                _pendingBytes += length;
                return true;
            }
        }

        private async Task RunWorkerAsync(ChannelReader<PendingWrite> reader)
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    lock (_lock)
                    {
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
                }

                // The queue is empty. Handing the batch to the operating system is what makes it survive a process
                // crash. It costs a write syscall and no device flush, so it does not add wear to flash memory.
                try
                {
                    _writer.FlushToOperatingSystem();
                }
                catch (Exception ex)
                {
                    ILogger.LogExceptionCore(ex);
                }
            }
        }

        /// <summary>
        ///     Stops accepting frames, drains everything already accepted, and closes the files. Every caller observes the
        ///     same operation, so a concurrent caller does not return before the files are closed.
        /// </summary>
        private Task QuiesceAsync()
        {
            lock (_lock)
            {
                if (_quiesceTask != null) return _quiesceTask;
                _pending.Writer.TryComplete();
                return _quiesceTask = QuiesceCoreAsync();
            }
        }

        private async Task QuiesceCoreAsync()
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }

            // Anything still queued was never written; release it so no pooled array is leaked.
            while (_pending.Reader.TryRead(out var item))
                try
                {
                    item.Frame.Dispose();
                }
                catch (Exception ex)
                {
                    ILogger.LogExceptionCore(ex);
                }

            lock (_lock)
            {
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
