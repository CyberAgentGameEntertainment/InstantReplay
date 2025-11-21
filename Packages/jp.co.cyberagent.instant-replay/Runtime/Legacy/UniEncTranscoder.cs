using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;
using Object = UnityEngine.Object;

namespace InstantReplay
{
    internal class UniEncTranscoder : ITranscoder
    {
        private const int JpegDecodesPerFrame = 1;

        private readonly AudioEncoder _audioEncoder;
        private readonly int _channels;
        private readonly EncodingSystem _encodingSystem;
        private readonly ChannelWriter<Task<(byte[] data, double timestamp)>> _jpegReaderWriter;

        private readonly Task _muxAudioTask;
        private readonly Muxer _muxer;
        private readonly Task _muxVideoTask;
        private readonly Action _onAfterUpdate;
        private readonly SharedBufferPool _sharedBufferPool;
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
            _sharedBufferPool = new SharedBufferPool(0);

            _muxVideoTask = Task.Run(async () =>
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
                    ILogger.LogExceptionCore(ex);
                }
            });

            _muxAudioTask = Task.Run(async () =>
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
                    ILogger.LogExceptionCore(ex);
                }
            });

            // jpeg decoder channel
            ThreadPool.GetMinThreads(out var numWorkerThread, out _);

            var jpegReaderChannel = Channel.CreateBounded<Task<(byte[], double)>>(
                new BoundedChannelOptions(numWorkerThread)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true
                });

            var reader = jpegReaderChannel.Reader;
            _jpegReaderWriter = jpegReaderChannel.Writer;

            var encoderQueueChannel =
                Channel.CreateBounded<(SharedBuffer buffer, nint width, nint height, double timestamp)>(
                    new BoundedChannelOptions(32)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = true
                    });

            var writer = encoderQueueChannel.Writer;
            var encoderQueueReader = encoderQueueChannel.Reader;

            PlayerLoopEntryPoint.OnAfterUpdate += _onAfterUpdate = () =>
            {
                Texture2D tex = null;
                try
                {
                    for (var a = 0;
                         a < JpegDecodesPerFrame && reader.TryPeek(out var peekTask) && peekTask.IsCompleted;
                         a++)
                    {
                        // check if we can write to encoder queue
                        var waitToWriteAsync = writer.WaitToWriteAsync();
                        try
                        {
                            if (!waitToWriteAsync.IsCompleted)
                                return;
                        }
                        finally
                        {
                            // forget
                            var awaiter = waitToWriteAsync.GetAwaiter();
                            awaiter.UnsafeOnCompleted(PooledActionOnce<ValueTaskAwaiter<bool>>
                                .Get(static awaiter => { awaiter.GetResult(); }, awaiter).Wrapper);
                        }

                        if (!reader.TryRead(out var task)) return;

                        var (bytes, timestamp) = task.Result;
                        tex ??= new Texture2D(2, 2);
                        if (!tex.LoadImage(bytes))
                            throw new Exception("Failed to load image from file");

                        // tex is now RGB24
                        // convert from RGB24 to BGRA32
                        var outputLength = tex.width * tex.height * 4;

                        if (!_sharedBufferPool.TryAlloc((nuint)outputLength, out var buffer))
                            throw new InvalidOperationException("Shared buffer pool exhausted.");

                        var data = tex.GetRawTextureData<byte>();
                        var output = buffer.Span;

                        for (var y = 0; y < tex.height; y++)
                        for (var x = 0; x < tex.width; x++)
                        {
                            // flip
                            var iIn = x + y * tex.width;
                            var iOut = x + (tex.height - y - 1) * tex.width;
                            output[iOut * 4 + 0] = data[iIn * 3 + 2];
                            output[iOut * 4 + 1] = data[iIn * 3 + 1];
                            output[iOut * 4 + 2] = data[iIn * 3 + 0];
                            output[iOut * 4 + 3] = 255;
                        }

                        if (!writer.TryWrite((buffer, width, height, timestamp)))
                        {
                            buffer.Dispose();
                            throw new InvalidOperationException("Failed to enqueue frame to encoder queue.");
                        }
                    }

                    if (reader.Completion.IsCompleted)
                        writer.TryComplete();
                }
                finally
                {
                    if (tex) Object.Destroy(tex);
                }
            };

            Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        Exception exception = null;
                        await foreach (var item in encoderQueueReader.ReadAllAsync().ConfigureAwait(false))
                            try
                            {
                                var buffer = item.buffer;
                                using (buffer)
                                {
                                    if (exception == null)
                                        await _videoEncoder.PushFrameAsync(buffer, (uint)item.width,
                                            (uint)item.height, item.timestamp).ConfigureAwait(false);
                                }
                            }
                            catch (Exception ex)
                            {
                                exception = ex;
                            }

                        if (exception != null)
                            throw exception;
                    }
                    finally
                    {
                        _videoEncoder.CompleteInput();
                    }
                }
                catch (Exception ex)
                {
                    ILogger.LogExceptionCore(ex);
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return default;

            if (_onAfterUpdate != null)
                PlayerLoopEntryPoint.OnAfterUpdate -= _onAfterUpdate;

            _muxer.Dispose();
            _videoEncoder.Dispose();
            _audioEncoder.Dispose();
            _encodingSystem.Dispose();
            return default;
        }

        public async ValueTask PushFrameAsync(string path, double timestamp, CancellationToken ct = default)
        {
            await _jpegReaderWriter.WriteAsync(
                Task.Run(async () => (await File.ReadAllBytesAsync(path, ct), timestamp), ct), ct);
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

        public ValueTask CompleteVideoAsync()
        {
            _jpegReaderWriter.TryComplete();
            return default;
        }

        public ValueTask CompleteAudioAsync()
        {
            _audioEncoder.CompleteInput();
            return default;
        }

        public async ValueTask CompleteAsync()
        {
            await _muxVideoTask;
            await _muxAudioTask;
            await _muxer.CompleteAsync();
            _sharedBufferPool.Dispose();
        }
    }
}
