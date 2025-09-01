using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UniEnc;
using Unity.Collections;
using UnityEngine;

namespace InstantReplay
{
    internal class UniEncTranscoder : ITranscoder
    {
        private readonly AudioEncoder _audioEncoder;
        private readonly int _channels;
        private readonly EncodingSystem _encodingSystem;
        private readonly Muxer _muxer;
        private readonly VideoEncoder _videoEncoder;
        private ulong _audioTimestampInSamples;
        private int _disposed;

        public UniEncTranscoder(int width, int height, int sampleRate, int channels, string outputFilename)
        {
            _channels = channels;
            _encodingSystem = new EncodingSystem(
                new VideoEncoderOptions
                {
                    Width = checked((uint)width),
                    Height = checked((uint)height),
                    Bitrate = (uint)Mathf.Min(width * height * 30 * 0.2f - 25000,
                        width * height * 30 * 0.1f + 1000),
                    FpsHint = 30
                },
                new AudioEncoderOptions
                {
                    Channels = checked((uint)channels),
                    SampleRate = checked((uint)sampleRate),
                    Bitrate = 128000
                });

            _videoEncoder = _encodingSystem.CreateVideoEncoder();
            _audioEncoder = _encodingSystem.CreateAudioEncoder();
            _muxer = _encodingSystem.CreateMuxer(outputFilename);

            Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        do
                        {
                            using var frame = await _videoEncoder.PullFrameAsync();
                            if (frame.Data.IsEmpty)
                                return;

                            await _muxer.PushVideoDataAsync(frame);
                        } while (true);
                    }
                    finally
                    {
                        await _muxer.FinishVideoAsync();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            });

            Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        do
                        {
                            using var frame = await _audioEncoder.PullFrameAsync();
                            if (frame.Data.IsEmpty)
                                return;

                            await _muxer.PushAudioDataAsync(frame);
                        } while (true);
                    }
                    finally
                    {
                        await _muxer.FinishAudioAsync();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return default;

            _muxer.Dispose();
            _videoEncoder.Dispose();
            _audioEncoder.Dispose();
            _encodingSystem.Dispose();
            return default;
        }

        public async ValueTask PushFrameAsync(string path, double timestamp, CancellationToken ct = default)
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);

            var (data, width, height, pitch) = UniEnc.Utils.DecodeJpeg(bytes,
                static (data, width, height, pitch, _) =>
                {
                    var length = height * pitch;
                    var expectedLength = width * height * 4;

                    var array = new NativeArray<byte>(data.Length, Allocator.Persistent);

                    if (data.Length == expectedLength)
                        data.CopyTo(array.AsSpan());
                    else
                        for (var y = 0; y < height; y++)
                            data.Slice((int)(y * pitch), (int)(width * 4))
                                .CopyTo(array.AsSpan().Slice((int)(y * width * 4), (int)(width * 4)));

                    return (array, width, height, pitch);
                }, 0);

            using (data)
            {
                await _videoEncoder.PushFrameAsync(data, (uint)width, (uint)height, timestamp);
            }
        }

        public async ValueTask PushAudioSamplesAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var length = buffer.Length / 2;
            var array = ArrayPool<short>.Shared.Rent(length);
            var timestamp = _audioTimestampInSamples;
            _audioTimestampInSamples += (ulong)(length / _channels);
            try
            {
                var arraySpan = array.AsMemory(0, length);
                MemoryMarshal.Cast<byte, short>(buffer.Span).CopyTo(array);
                await _audioEncoder.PushSamplesAsync(arraySpan, timestamp);
            }
            finally
            {
                ArrayPool<short>.Shared.Return(array);
            }
        }

        public ValueTask CompleteAsync()
        {
            _videoEncoder.CompleteInput();
            _audioEncoder.CompleteInput();
            return _muxer.CompleteAsync();
        }
    }
}
