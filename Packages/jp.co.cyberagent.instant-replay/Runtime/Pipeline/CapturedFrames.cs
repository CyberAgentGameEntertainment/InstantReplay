// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Encoded frames taken out of a buffer that can be muxed any number of times, each time over a sub-range.
    /// </summary>
    /// <remarks>
    ///     Sample timestamps are relative to the first selected sample of each stream, in the same way as an export.
    ///     <see cref="CreateSelection" /> returns readers that the caller owns and that never consume the held frames.
    /// </remarks>
    internal abstract class CapturedFrames : IDisposable
    {
        public const double Epsilon = 1e-6;

        public abstract IReadOnlyList<EncodedFrameDescriptor> Video { get; }
        public abstract IReadOnlyList<EncodedFrameDescriptor> Audio { get; }

        public abstract void Dispose();

        /// <summary>
        ///     Creates readers for the video samples in [videoStart, videoEnd) and the audio samples in
        ///     [audioStart, audioEnd), each preceded by the configuration records of its stream and rebased so that the
        ///     first sample starts at zero.
        /// </summary>
        public abstract EncodedFrameSelection CreateSelection(int videoStart, int videoEnd, int audioStart,
            int audioEnd);

        /// <summary>
        ///     Returns the seconds from the first video sample to the latest one. The latest one is not always the last
        ///     sample: MediaCodec (Android) may produce a frame with timestamp zero at the end of the stream.
        /// </summary>
        public double GetDurationSeconds()
        {
            var video = Video;
            var duration = 0.0;
            for (var i = 0; i < video.Count; i++)
                duration = Math.Max(duration, video[i].Timestamp);
            return duration;
        }

        /// <summary>
        ///     Finds the samples to mux for a range in seconds. The video starts at the latest key frame at or before
        ///     <paramref name="startSeconds" />, and the audio covers the same span of time.
        /// </summary>
        /// <exception cref="ArgumentException">The range contains no video frame.</exception>
        public void ResolveRange(double startSeconds, double? endSeconds, out int videoStart, out int videoEnd,
            out int audioStart, out int audioEnd)
        {
            var video = Video;
            var audio = Audio;

            videoStart = 0;
            for (var i = 0; i < video.Count; i++)
            {
                if (video[i].Kind != UniencSampleKind.Key) continue;
                if (video[i].Timestamp > startSeconds + Epsilon) break;
                videoStart = i;
            }

            if (video.Count == 0 || endSeconds <= video[videoStart].Timestamp + Epsilon)
                throw new ArgumentException("The range contains no video frame.", nameof(endSeconds));

            videoEnd = video.Count;
            if (endSeconds is { } end)
                for (var i = videoStart + 1; i < video.Count; i++)
                    if (video[i].Timestamp >= end - Epsilon)
                    {
                        videoEnd = i;
                        break;
                    }

            var startTime = video[videoStart].Timestamp;
            var endTime = videoEnd < video.Count ? video[videoEnd].Timestamp : double.PositiveInfinity;

            audioStart = audio.Count;
            for (var i = 0; i < audio.Count; i++)
                if (audio[i].Timestamp >= startTime - Epsilon)
                {
                    audioStart = i;
                    break;
                }

            audioEnd = audio.Count;
            for (var i = audioStart; i < audio.Count; i++)
                if (audio[i].Timestamp >= endTime - Epsilon)
                {
                    audioEnd = i;
                    break;
                }
        }

        protected static EncodedFrameDescriptor[] Rebase(IReadOnlyList<EncodedFrameDescriptor> descriptors)
        {
            var result = new EncodedFrameDescriptor[descriptors.Count];
            var origin = descriptors.Count > 0 ? descriptors[0].Timestamp : 0;
            for (var i = 0; i < result.Length; i++)
                result[i] = new EncodedFrameDescriptor(descriptors[i].Timestamp - origin, descriptors[i].Kind);
            return result;
        }
    }

    /// <summary>
    ///     Frames held in memory. They are owned by this object and copied for every mux, because the muxer releases
    ///     each frame it receives.
    /// </summary>
    internal sealed class InMemoryCapturedFrames : CapturedFrames
    {
        private readonly EncodedFrame[] _audio;
        private readonly EncodedFrameDescriptor[] _audioDescriptors;
        private readonly EncodedFrame[] _audioMetadata;
        private readonly Action<Exception> _onError;
        private readonly EncodedFrame[] _video;
        private readonly EncodedFrameDescriptor[] _videoDescriptors;
        private readonly EncodedFrame[] _videoMetadata;
        private bool _disposed;

        /// <param name="video">Video frames as selected for an export: configuration records first, then samples.</param>
        /// <param name="audio">Audio frames in the same layout.</param>
        public InMemoryCapturedFrames(ReadOnlySpan<EncodedFrame> video, ReadOnlySpan<EncodedFrame> audio,
            Action<Exception> onError = null)
        {
            _onError = onError;
            Split(video, out _videoMetadata, out _video);
            Split(audio, out _audioMetadata, out _audio);
            _videoDescriptors = Rebase(EncodedFrameSelector.ToDescriptors(_video));
            _audioDescriptors = Rebase(EncodedFrameSelector.ToDescriptors(_audio));
        }

        public override IReadOnlyList<EncodedFrameDescriptor> Video => _videoDescriptors;
        public override IReadOnlyList<EncodedFrameDescriptor> Audio => _audioDescriptors;

        public override EncodedFrameSelection CreateSelection(int videoStart, int videoEnd, int audioStart,
            int audioEnd)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(InMemoryCapturedFrames));
            return new EncodedFrameSelection(
                new CopyingReader(_videoMetadata,
                    new ArraySegment<EncodedFrame>(_video, videoStart, videoEnd - videoStart)),
                new CopyingReader(_audioMetadata,
                    new ArraySegment<EncodedFrame>(_audio, audioStart, audioEnd - audioStart)));
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EncodedFrameSelector.DisposeAll(_videoMetadata, _onError);
            EncodedFrameSelector.DisposeAll(_video, _onError);
            EncodedFrameSelector.DisposeAll(_audioMetadata, _onError);
            EncodedFrameSelector.DisposeAll(_audio, _onError);
        }

        private static void Split(ReadOnlySpan<EncodedFrame> frames, out EncodedFrame[] metadata,
            out EncodedFrame[] samples)
        {
            var count = 0;
            while (count < frames.Length && frames[count].Kind == UniencSampleKind.Metadata) count++;
            metadata = frames[..count].ToArray();
            samples = frames[count..].ToArray();
        }

        private sealed class CopyingReader : IEncodedFrameReader
        {
            private readonly EncodedFrame[] _metadata;
            private readonly double _origin;
            private readonly ArraySegment<EncodedFrame> _samples;
            private int _position;

            public CopyingReader(EncodedFrame[] metadata, ArraySegment<EncodedFrame> samples)
            {
                _metadata = metadata;
                _samples = samples;
                _origin = samples.Count > 0 ? samples.Array[samples.Offset].Timestamp : 0;
            }

            public bool TryReadNext(out EncodedFrame frame)
            {
                if (_position < _metadata.Length)
                {
                    var source = _metadata[_position++];
                    frame = EncodedFrame.CreateWithCopy(source.Data, source.Timestamp, source.Kind);
                    return true;
                }

                var index = _position - _metadata.Length;
                if (index < _samples.Count)
                {
                    _position++;
                    var source = _samples.Array[_samples.Offset + index];
                    frame = EncodedFrame.CreateWithCopy(source.Data, source.Timestamp - _origin, source.Kind);
                    return true;
                }

                frame = default;
                return false;
            }

            public void Dispose()
            {
                _position = int.MaxValue;
            }
        }
    }

    /// <summary>
    ///     Frames whose payloads are held in the <see cref="EncodedFrameTrackStorage" /> of each stream of an in-memory
    ///     buffer. The storages are no longer written to, and they are released together with this object.
    /// </summary>
    internal sealed class TrackStorageCapturedFrames : CapturedFrames
    {
        private readonly ArraySegment<StoredEncodedFrame> _audio;
        private readonly EncodedFrameDescriptor[] _audioDescriptors;
        private readonly EncodedFrame[] _audioMetadata;
        private readonly Action<Exception> _onError;
        private readonly ArraySegment<StoredEncodedFrame> _video;
        private readonly EncodedFrameDescriptor[] _videoDescriptors;
        private readonly EncodedFrame[] _videoMetadata;
        private EncodedFrameTrackStorage _audioStorage;
        private bool _disposed;
        private EncodedFrameTrackStorage _videoStorage;

        /// <param name="videoMetadata">Configuration records of the video stream, which this object takes over.</param>
        /// <param name="audioMetadata">Configuration records of the audio stream, which this object takes over.</param>
        public TrackStorageCapturedFrames(EncodedFrameTrackStorage videoStorage, EncodedFrame[] videoMetadata,
            ArraySegment<StoredEncodedFrame> video, EncodedFrameTrackStorage audioStorage,
            EncodedFrame[] audioMetadata, ArraySegment<StoredEncodedFrame> audio, Action<Exception> onError = null)
        {
            _videoStorage = videoStorage;
            _videoMetadata = videoMetadata;
            _video = video;
            _audioStorage = audioStorage;
            _audioMetadata = audioMetadata;
            _audio = audio;
            _onError = onError;
            _videoDescriptors = Rebase(EncodedFrameSelector.ToDescriptors(video.AsSpan()));
            _audioDescriptors = Rebase(EncodedFrameSelector.ToDescriptors(audio.AsSpan()));
        }

        public override IReadOnlyList<EncodedFrameDescriptor> Video => _videoDescriptors;
        public override IReadOnlyList<EncodedFrameDescriptor> Audio => _audioDescriptors;

        public override EncodedFrameSelection CreateSelection(int videoStart, int videoEnd, int audioStart,
            int audioEnd)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TrackStorageCapturedFrames));
            return new EncodedFrameSelection(
                new TrackStorageFrameReader(_videoStorage, _videoMetadata, false,
                    _video.Slice(videoStart, videoEnd - videoStart)),
                new TrackStorageFrameReader(_audioStorage, _audioMetadata, false,
                    _audio.Slice(audioStart, audioEnd - audioStart)));
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Released explicitly, since the capture may stay referenced after it is disposed.
            _videoStorage = null;
            _audioStorage = null;
            EncodedFrameSelector.DisposeAll(_videoMetadata, _onError);
            EncodedFrameSelector.DisposeAll(_audioMetadata, _onError);
        }
    }

    /// <summary>
    ///     Frames left in the segment files of a disk buffer. Only the location of every frame is held in memory, and the
    ///     payloads are read while muxing. The session directory is deleted when this object is disposed.
    /// </summary>
    internal sealed class DiskCapturedFrames : CapturedFrames
    {
        private readonly List<DiskBufferScannedRecord> _audio;
        private readonly EncodedFrameDescriptor[] _audioDescriptors;
        private readonly Action<Exception> _onError;
        private readonly Action _release;
        private readonly DiskBufferScanResult _scan;
        private readonly List<DiskBufferScannedRecord> _video;
        private readonly EncodedFrameDescriptor[] _videoDescriptors;
        private bool _disposed;

        /// <param name="release">Deletes the session directory and lets recovery see it no longer.</param>
        public DiskCapturedFrames(DiskBufferScanResult scan, List<DiskBufferScannedRecord> video,
            List<DiskBufferScannedRecord> audio, Action release, Action<Exception> onError = null)
        {
            _onError = onError;
            _scan = scan;
            _video = video;
            _audio = audio;
            _release = release;
            _videoDescriptors = Rebase(EncodedFrameSelector.ToDescriptors(video));
            _audioDescriptors = Rebase(EncodedFrameSelector.ToDescriptors(audio));
        }

        public override IReadOnlyList<EncodedFrameDescriptor> Video => _videoDescriptors;
        public override IReadOnlyList<EncodedFrameDescriptor> Audio => _audioDescriptors;

        public override EncodedFrameSelection CreateSelection(int videoStart, int videoEnd, int audioStart,
            int audioEnd)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiskCapturedFrames));
            return new EncodedFrameSelection(
                new DiskBufferRecordReader(_scan.VideoMetadata, _video.GetRange(videoStart, videoEnd - videoStart),
                    _onError),
                new DiskBufferRecordReader(_scan.AudioMetadata, _audio.GetRange(audioStart, audioEnd - audioStart),
                    _onError));
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _release?.Invoke();
        }
    }
}
