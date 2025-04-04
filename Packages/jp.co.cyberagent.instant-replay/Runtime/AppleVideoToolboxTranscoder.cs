// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AOT;
using UnityEngine;

namespace InstantReplay
{
    internal class AppleVideoToolboxTranscoder : ITranscoder
    {
        private const string LibraryName =
#if !UNITY_EDITOR && UNITY_IOS
                "__Internal"
#else
                "libInstantReplayTranscoder"
#endif
            ;

        // these fields are required to prevent the delegate from being garbage collected
        private static PullSampleBuffer _pullVideoSampleBuffer;
        private static PullSampleBuffer _pullAudioSampleBuffer;
        private static Action<nint, nint, nint> _onEncoded;
        private static Action<nint, nint> _onCompleteDelegate;

        private static nint? _onCompleteDelegatePtr;
        private readonly ChannelReader<nint> _audioSampleBufferReader;
        private readonly ChannelWriter<nint> _audioSampleBufferWriter;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<bool> _completionSource = new();
        private readonly ChannelReader<nint> _encodedVideoSampleBufferReader;
        private readonly ChannelWriter<nint> _encodedVideoSampleBufferWriter;
        private readonly ChannelWriter<FrameLoadRequest> _loadedPixelBufferWriter;

        private readonly object _transcoderLock = new();

        private int _activeTracks = 2;

        // used to check if the encoded video sample buffer channel can be completed
        private int _numEncodingFrames;

        private nint _transcoderPtr;

        public AppleVideoToolboxTranscoder(int outputWidth, int outputHeight, int sampleRate, int channels,
            string outputFilename)
        {
            // We use the channel to make sure that the frame is already loaded from the disk before encoding it.
            // It prepares up to 5 frames in advance.

            // Strategy
            // - Use VideoToolbox to encode video frames.
            // - Use AVAssetWriter to mux.
            //   - AVAssetWriter also supports direct input of raw audio data.
            // - AVAssetWriter needs to interleave audio and video samples. AVAssetWriter pulls samples with the callbacks when it wants, and we need to provide them.
            // - We use the channels below to queue up the audio and video samples:
            //   - The audio sample buffer channel (bounded with capacity 5). We transform raw audio data into CMSampleBuffers and queue them up.
            //   - The video frame channel (bounded with capacity 5). We transform JPEG images into CVPixelBuffers and queue them up.
            //   - The encoded video sample buffer channel (unbounded). We encode CVPixelBuffers into CMSampleBuffers and queue them up.

            var channel = Channel.CreateBounded<FrameLoadRequest>(new BoundedChannelOptions(5)
            {
                AllowSynchronousContinuations = true,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

            _loadedPixelBufferWriter = channel.Writer;

            var encodedVideoSampleBufferChannel = Channel.CreateUnbounded<nint>(new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = true,
                SingleReader = false,
                SingleWriter = true
            });

            _encodedVideoSampleBufferWriter = encodedVideoSampleBufferChannel.Writer;
            _encodedVideoSampleBufferReader = encodedVideoSampleBufferChannel.Reader;

            var audioSampleBufferChannel = Channel.CreateBounded<nint>(new BoundedChannelOptions(5)
            {
                AllowSynchronousContinuations = true,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

            _audioSampleBufferWriter = audioSampleBufferChannel.Writer;
            _audioSampleBufferReader = audioSampleBufferChannel.Reader;

            var handle = GCHandle.Alloc(this);
            var handlePtr = GCHandle.ToIntPtr(handle);
            _transcoderPtr = InstantReplay_CreateSession(
                outputWidth,
                outputHeight,
                sampleRate,
                channels,
                outputFilename,
                Marshal.GetFunctionPointerForDelegate(Utils.HoldDelegate(ref _pullVideoSampleBuffer,
                    static () => PullVideoSampleBuffer)),
                handlePtr,
                Marshal.GetFunctionPointerForDelegate(Utils.HoldDelegate(ref _pullAudioSampleBuffer,
                    static () => PullAudioSampleBuffer)),
                handlePtr
            );
            if (_transcoderPtr == 0) throw new InvalidOperationException();

            Task.Run(async () =>
            {
                try
                {
                    await EncodeVideoFramesAsync(channel.Reader, _cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeCore(_cancellation, _completionSource);
            GC.SuppressFinalize(this);
        }

        public async ValueTask PushFrameAsync(string path, double timestamp, CancellationToken ct = default)
        {
            ct = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellation.Token).Token;
            var transcoderPtr = _transcoderPtr;
            var pixelBufferPtr = InstantReplay_LoadJpeg(transcoderPtr, path);
            await _loadedPixelBufferWriter.WriteAsync(new FrameLoadRequest(pixelBufferPtr, timestamp), ct)
                .ConfigureAwait(false);
        }

        public async ValueTask PushAudioSamplesAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await _audioSampleBufferWriter.WaitToWriteAsync(ct);
            nint sampleBuffer;
            lock (_transcoderLock)
            {
                unsafe
                {
                    fixed (byte* bufferPtr = buffer.Span)
                    {
                        sampleBuffer = InstantReplay_CreateAudioSampleBuffer(_transcoderPtr, bufferPtr, buffer.Length);
                    }
                }
            }

            await _audioSampleBufferWriter.WriteAsync(sampleBuffer, ct);
        }

        public async ValueTask CompleteAsync()
        {
            _loadedPixelBufferWriter.TryComplete();
            _audioSampleBufferWriter.TryComplete();
            await _completionSource.Task.ConfigureAwait(false);
        }

        private async ValueTask EncodeVideoFramesAsync(ChannelReader<FrameLoadRequest> reader,
            CancellationToken ct = default)
        {
            try
            {
                _numEncodingFrames = 1;

                await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    // VideoToolbox encodes frames asynchronously and doesn't emit output frames in the order we provided (present order) but in the order they are encoded (decode order).
                    // It means that we cannot restrict numbers of concurrent processed frames because the encoder may require more than one frame to produce the next frame.
                    // Instead, we pause the encoder when there are too many encoded frames to be sent to the AVAssetWriter.
                    while (_encodedVideoSampleBufferReader.Count >= 5)
                    {
                        // buffer is full
                        await Task.Yield();
                        ct.ThrowIfCancellationRequested();
                    }

                    lock (_transcoderLock)
                    {
                        var transcoderPtr = _transcoderPtr;
                        if (transcoderPtr == 0)
                            throw new InvalidOperationException("Transcoder is already disposed.");

                        [MonoPInvokeCallback(typeof(Action<nint, nint, nint>))]
                        static void OnEncoded(nint ctx, nint result, nint error)
                        {
                            try
                            {
                                var handle = GCHandle.FromIntPtr(ctx);
                                var @this = handle.Target as AppleVideoToolboxTranscoder;
                                handle.Free();
                                try
                                {
                                    if (error != 0)
                                        throw new Exception(Marshal.PtrToStringAnsi(error));

                                    while (!@this._encodedVideoSampleBufferWriter.TryWrite(result))
                                        // spin
                                        Thread.Yield();
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogException(ex);
                                }
                                finally
                                {
                                    if (Interlocked.Decrement(ref @this._numEncodingFrames) == 0)
                                        @this._encodedVideoSampleBufferWriter.Complete();
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.LogException(ex);
                            }
                        }

                        var thisHandle = GCHandle.Alloc(this);

                        var onEncoded =
                            Marshal.GetFunctionPointerForDelegate(Utils.HoldDelegate(ref _onEncoded,
                                static () => OnEncoded));
                        Interlocked.Increment(ref _numEncodingFrames);
                        InstantReplay_EncodeVideoFrame(transcoderPtr, frame._pixelBuffer, frame._timestamp, onEncoded,
                            GCHandle.ToIntPtr(thisHandle));
                    }
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _numEncodingFrames) == 0)
                    _encodedVideoSampleBufferWriter.Complete();

                lock (_transcoderLock)
                {
                    if (InstantReplay_CompleteVideoFrames(_transcoderPtr) != 0)
                        throw new InvalidOperationException("Failed to complete video frames");
                }
            }
        }

        private void CompleteTrack(GCHandle thisHandle)
        {
            if (Interlocked.Decrement(ref _activeTracks) == 0)
                // both audio and video tracks are completed
                Task.Run(() =>
                {
                    lock (_transcoderLock)
                    {
                        var transcoderPtr = _transcoderPtr;
                        if (transcoderPtr == 0)
                            throw new InvalidOperationException();

                        _transcoderPtr = 0;

                        var onCompleteDelegate = Utils.HoldDelegate(ref _onCompleteDelegate, static () => OnComplete);
                        var callback = _onCompleteDelegatePtr ??=
                            Marshal.GetFunctionPointerForDelegate(onCompleteDelegate);

                        InstantReplay_Complete(transcoderPtr, callback, GCHandle.ToIntPtr(thisHandle));
                    }
                });
        }

        [MonoPInvokeCallback(typeof(PullSampleBuffer))]
        private static PullState PullVideoSampleBuffer(nint context, out nint sampleBuffer)
        {
            var handle = GCHandle.FromIntPtr(context);
            var @this = handle.Target as AppleVideoToolboxTranscoder;

            try
            {
                var reader = @this._encodedVideoSampleBufferReader;

                if (reader.Completion.IsCompleted)
                {
                    @this.CompleteTrack(handle);
                    sampleBuffer = 0;
                    return PullState.Completed;
                }

                try
                {
                    sampleBuffer = reader.ReadAsync().AsTask().Result;
                    return PullState.Pulled;
                }
                catch (AggregateException ex) when (ex.InnerExceptions.Any(static ex => ex is ChannelClosedException))
                {
                    @this.CompleteTrack(handle);
                    sampleBuffer = 0;
                    return PullState.Completed;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                sampleBuffer = 0;
                return PullState.Completed;
            }
        }

        [MonoPInvokeCallback(typeof(PullSampleBuffer))]
        private static PullState PullAudioSampleBuffer(nint context, out nint sampleBuffer)
        {
            var handle = GCHandle.FromIntPtr(context);
            var @this = handle.Target as AppleVideoToolboxTranscoder;
            try
            {
                var reader = @this._audioSampleBufferReader;

                if (reader.Completion.IsCompleted)
                {
                    @this.CompleteTrack(handle);
                    sampleBuffer = 0;
                    return PullState.Completed;
                }

                try
                {
                    sampleBuffer = reader.ReadAsync().AsTask().Result;
                    return PullState.Pulled;
                }
                catch (AggregateException ex) when (ex.InnerExceptions.Any(static ex => ex is ChannelClosedException))
                {
                    @this.CompleteTrack(handle);
                    sampleBuffer = 0;
                    return PullState.Completed;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                sampleBuffer = 0;
                return PullState.Continues;
            }
        }

        private static async ValueTask DisposeCore(CancellationTokenSource cancellation,
            TaskCompletionSource<bool> completionSource)
        {
            cancellation.Cancel();
            await completionSource.Task.ConfigureAwait(false);
        }

        [MonoPInvokeCallback(typeof(Action<nint, nint>))]
        private static void OnComplete(nint context, nint error)
        {
            try
            {
                var handle = GCHandle.FromIntPtr(context);
                var @this = handle.Target as AppleVideoToolboxTranscoder;
                handle.Free();

                if (error != 0)
                    try
                    {
                        throw new Exception(Marshal.PtrToStringAnsi(error));
                    }
                    catch (Exception ex)
                    {
                        @this!._completionSource.TrySetException(ex);
                    }
                else
                    @this!._completionSource.TrySetResult(false);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        [DllImport(LibraryName, CharSet = CharSet.Ansi)]
        private static extern nint InstantReplay_CreateSession(
            int width,
            int height,
            nint sampleRate,
            int channels,
            string destination,
            nint /* @convention(c) (UnsafeRawPointer) -> UnsafePointer<CMSampleBuffer>? */ pullVideoSampleBuffer,
            nint pullVideoSampleBufferCtx,
            nint /* @convention(c) (UnsafeRawPointer) -> UnsafePointer<CMSampleBuffer>? */ pullAudioSampleBuffer,
            nint pullAudioSampleBufferCtx);

        [DllImport(LibraryName, CharSet = CharSet.Ansi)]
        private static extern nint InstantReplay_LoadJpeg(nint transcoderPtr, string filename);

        [DllImport(LibraryName)]
        private static extern void InstantReplay_EncodeVideoFrame(
            nint transcoderPtr,
            nint pixelBufferPtr,
            double timestamp,
            nint /* @convention(c) (UnsafeRawPointer /* ctx * /, UnsafeRawPointer? /* result * /, UnsafePointer<CChar>? /* error * /) -> Void */
                callback,
            nint callbackCtx);

        [DllImport(LibraryName)]
        private static extern int InstantReplay_CompleteVideoFrames(
            nint transcoderPtr);

        [DllImport(LibraryName)]
        private static extern unsafe nint InstantReplay_CreateAudioSampleBuffer(nint transcoderPtr, byte* audioSamples, nint length);


        [DllImport(LibraryName)]
        private static extern int InstantReplay_Complete(nint transcoderPtr, nint callback, nint context);

        private enum PullState
        {
            Pulled = 0,
            Continues = 1,
            Completed = -1
        }

        private delegate PullState PullSampleBuffer(nint context, out nint sampleBuffer);

        private readonly struct FrameLoadRequest
        {
            public readonly nint _pixelBuffer;
            public readonly double _timestamp;

            public FrameLoadRequest(nint pixelBuffer, double timestamp)
            {
                _pixelBuffer = pixelBuffer;
                _timestamp = timestamp;
            }
        }
    }
}
