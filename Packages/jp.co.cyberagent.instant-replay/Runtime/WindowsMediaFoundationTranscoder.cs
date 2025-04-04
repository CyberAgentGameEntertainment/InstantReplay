// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AOT;
using UnityEngine;

namespace InstantReplay
{
    internal class WindowsMediaFoundationTranscoder : ITranscoder
    {
        private const string LibraryName = "instant_replay_transcoder";
        private readonly PipeWriter _audioPipeWriter;

        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<bool> _completionSource = new();
        private readonly ChannelWriter<FrameLoadRequest> _loadedImageWriter;
        private Thread _transcoderThread;

        public WindowsMediaFoundationTranscoder(int outputWidth, int outputHeight, int sampleRate, int channels,
            string outputFilename)
        {
            // We use the channel to make sure that the frame is already loaded from the disk before encoding it.
            // It prepares up to 5 frames in advance.
            var frameLoadRequestChannel = Channel.CreateBounded<FrameLoadRequest>(new BoundedChannelOptions(5)
            {
                AllowSynchronousContinuations = true,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

            var audioPipe = new Pipe();
            _audioPipeWriter = audioPipe.Writer;
            var audioPipeReader = audioPipe.Reader;

            var reader = frameLoadRequestChannel.Reader;

            // to avoid COM initialization issues, we create an individual thread for the transcoder
            new Thread(() =>
            {
                try
                {
                    var ct = _cancellation.Token;
                    var transcoder = NativeMethods.Create((uint)outputWidth, (uint)outputHeight, 30,
                        (uint)(outputWidth * outputHeight * 4), checked((uint)sampleRate), checked((uint)channels),
                        outputFilename);
                    if (transcoder == 0) throw new InvalidOperationException();

                    // start audio thread
                    var audioThread = new Thread(() =>
                    {
                        try
                        {
                            long wroteSamples = 0;
                            var lifetime = NativeMethods.NewMfLifetimeForThread(); // initialize for this thread
                            try
                            {
                                var audioReadResult = new ReadResult(ReadOnlySequence<byte>.Empty, false, false);
                                do
                                {
                                    ct.ThrowIfCancellationRequested();
                                    if (!audioPipeReader.TryRead(out audioReadResult))
                                    {
                                        Thread.Yield(); // spin
                                        continue;
                                    }

                                    var buffer = audioReadResult.Buffer;
                                    foreach (var segment in buffer)
                                    {
                                        var bufferShort =
                                            MemoryMarshal
                                                .Cast<byte, short>(segment.Span); // NOTE: is this alignment safe?
                                        NativeMethods.PushAudioSamples(transcoder, bufferShort,
                                            wroteSamples / (double)sampleRate);
                                        wroteSamples += bufferShort.Length / channels;
                                    }

                                    audioPipeReader.AdvanceTo(buffer.End);
                                } while (!audioReadResult.IsCompleted);
                            }
                            finally
                            {
                                NativeMethods.DropMfLifetime(lifetime);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogException(ex);
                        }
                    });
                    audioThread.Start();

                    try
                    {
                        var completion = reader.Completion;

                        while (!completion.IsCompleted)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!reader.TryRead(out var frame))
                            {
                                Thread.Yield(); // spin
                                continue;
                            }

                            NativeMethods.PushFrame(transcoder, frame._image, frame._timestamp);
                        }
                    }
                    finally
                    {
                        audioThread.Join();
                        NativeMethods.Complete(transcoder);
                    }
                }
                catch (Exception ex)
                {
                    _completionSource.TrySetException(ex);
                    _cancellation.Cancel();
                    throw;
                }
                finally
                {
                    audioPipeReader.Complete();
                }

                _completionSource.TrySetResult(false);
            }).Start();

            _loadedImageWriter = frameLoadRequestChannel.Writer;
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeCore(_cancellation, _completionSource);
            GC.SuppressFinalize(this);
        }

        public async ValueTask PushFrameAsync(string path, double timestamp, CancellationToken ct = default)
        {
            ct = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellation.Token).Token;
            var image = NativeMethods.LoadFrame(path);
            await _loadedImageWriter.WriteAsync(new FrameLoadRequest(image, timestamp), ct).ConfigureAwait(false);
        }

        public async ValueTask PushAudioSamplesAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellation.Token).Token;
            await _audioPipeWriter.WriteAsync(buffer, ct);
        }

        public async ValueTask CompleteAsync()
        {
            _loadedImageWriter.TryComplete();
            await _audioPipeWriter.CompleteAsync();
            await _completionSource.Task.ConfigureAwait(false);
        }

        private static async ValueTask DisposeCore(CancellationTokenSource cancellation,
            TaskCompletionSource<bool> completionSource)
        {
            cancellation.Cancel();
            await completionSource.Task.ConfigureAwait(false);
        }

        private static class NativeMethods
        {
            [ThreadStatic] private static Exception _error;

            // required to prevent the delegate from being garbage collected
            private static Action<nint, nint> _onError;

            private static TResult ExecuteWithErrorContext<TState, TResult>(
                Func<TState, nint /* on_error */, nint /* ctx */, TResult> action, TState state)
            {
                var result = action(state,
                    Marshal.GetFunctionPointerForDelegate(Utils.HoldDelegate(ref _onError, static () => OnError)), 0);
                if (_error != null)
                {
                    var error = _error;
                    _error = null;
                    throw error;
                }

                return result;
            }

            private static void ExecuteWithErrorContext<TState>(
                Action<TState, nint /* on_error */, nint /* ctx */> action, TState state)
            {
                action(state, Marshal.GetFunctionPointerForDelegate<Action<nint, nint>>(OnError), 0);
                if (_error != null)
                {
                    var error = _error;
                    _error = null;
                    throw error;
                }
            }

            [MonoPInvokeCallback(typeof(Action<nint, nint>))]
            private static void OnError(nint context, nint message)
            {
                if (message == 0) return;
                try
                {
                    throw new Exception(Marshal.PtrToStringAnsi(message));
                }
                catch (Exception ex)
                {
                    _error = ex;
                }
            }

            public static nint Create(uint width, uint height, uint frame_rate, uint average_bitrate,
                uint audio_sample_rate, uint audio_channels, string output_path)
            {
                return ExecuteWithErrorContext(
                    static (state, on_error, ctx) => instant_replay_create(state.width, state.height, state.frame_rate,
                        state.average_bitrate, state.audio_sample_rate, state.audio_channels, state.output_path,
                        on_error, ctx),
                    (width, height, frame_rate, average_bitrate, audio_sample_rate, audio_channels, output_path));
            }

            public static nint LoadFrame(string path)
            {
                return ExecuteWithErrorContext(
                    static (path, on_error, ctx) => instant_replay_load_frame(path, on_error, ctx), path);
            }

            public static void PushFrame(nint transcoder, nint frame, double timestamp)
            {
                ExecuteWithErrorContext(
                    static (state, on_error, ctx) =>
                        instant_replay_push_frame(state.transcoder, state.frame, state.timestamp, on_error, ctx),
                    (transcoder, frame, timestamp));
            }

            public static void PushAudioSamples(nint transcoder, ReadOnlySpan<short> samples, double timestamp)
            {
                unsafe
                {
                    fixed (short* ptr = samples)
                    {
                        ExecuteWithErrorContext(
                            static (state, on_error, ctx) =>
                                instant_replay_push_audio_samples(state.transcoder, (short*)state.Item2, state.Length,
                                    state.timestamp, on_error, ctx),
                            (transcoder, (nint)ptr, samples.Length, timestamp));
                    }
                }
            }

            public static nint NewMfLifetimeForThread()
            {
                return ExecuteWithErrorContext(
                    static (_, on_error, ctx) => instant_replay_new_mf_lifetime_for_thread(on_error, ctx),
                    0);
            }

            public static void DropMfLifetime(nint lifetime)
            {
                ExecuteWithErrorContext(
                    static (lifetime, on_error, ctx) => instant_replay_drop_mf_lifetime(lifetime),
                    lifetime);
            }

            public static void CompleteFrames(nint transcoder)
            {
                ExecuteWithErrorContext(
                    static (transcoder, on_error, ctx) => instant_replay_complete_frames(transcoder, on_error, ctx),
                    transcoder);
            }

            public static void CompleteAudioSamples(nint transcoder)
            {
                ExecuteWithErrorContext(
                    static (transcoder, on_error, ctx) =>
                        instant_replay_complete_audio_samples(transcoder, on_error, ctx),
                    transcoder);
            }

            public static void Complete(nint transcoder)
            {
                ExecuteWithErrorContext(
                    static (transcoder, on_error, ctx) => instant_replay_complete(transcoder, on_error, ctx),
                    transcoder);
            }

            [DllImport(LibraryName, CharSet = CharSet.Ansi)]
            private static extern nint instant_replay_create(uint width, uint height, uint frame_rate,
                uint average_bitrate,
                uint audio_sample_rate, uint audio_channels, string output_path, nint on_error, nint ctx);

            [DllImport(LibraryName, CharSet = CharSet.Ansi)]
            private static extern nint instant_replay_load_frame(string path, nint on_error, nint ctx);

            [DllImport(LibraryName, CharSet = CharSet.Ansi)]
            private static extern void instant_replay_push_frame(nint transcoder, nint frame, double timestamp,
                nint on_error,
                nint ctx);

            [DllImport(LibraryName)]
            private static extern unsafe void instant_replay_push_audio_samples(nint transcoder, short* samples,
                nint count,
                double timestamp, nint on_error, nint ctx);

            [DllImport(LibraryName)]
            private static extern nint instant_replay_new_mf_lifetime_for_thread(nint on_error, nint ctx);

            [DllImport(LibraryName)]
            private static extern void instant_replay_drop_mf_lifetime(nint lifetime);

            [DllImport(LibraryName)]
            private static extern void instant_replay_complete_frames(nint transcoder, nint on_error, nint ctx);

            [DllImport(LibraryName)]
            private static extern void instant_replay_complete_audio_samples(nint transcoder, nint on_error, nint ctx);


            [DllImport(LibraryName)]
            private static extern void instant_replay_complete(nint transcoder, nint on_error, nint ctx);
        }

        private readonly struct FrameLoadRequest
        {
            public readonly nint _image;
            public readonly double _timestamp;

            public FrameLoadRequest(nint image, double timestamp)
            {
                _image = image;
                _timestamp = timestamp;
            }
        }
    }
}
