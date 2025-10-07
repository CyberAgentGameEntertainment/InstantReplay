using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UniEnc.Internal;

namespace UniEnc
{
    /// <summary>
    ///     Encodes raw audio samples to compressed format.
    /// </summary>
    public sealed class AudioEncoder : IDisposable
    {
        private readonly object _lock = new();
        private InputHandle _inputHandle;
        private OutputHandle _outputHandle;

        internal AudioEncoder(nint inputHandle, nint outputHandle)
        {
            _inputHandle = new InputHandle(inputHandle);
            _outputHandle = new OutputHandle(outputHandle);
        }

        /// <summary>
        ///     Releases all resources used by the audio encoder.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Pushes raw audio samples to the encoder.
        /// </summary>
        /// <param name="audioData">Raw audio samples (PCM int16 signed)</param>
        /// <param name="timestampInSamples">Timestamp in samples since start</param>
        public ValueTask PushSamplesAsync(ReadOnlyMemory<short> audioData, ulong timestampInSamples)
        {
            lock (_lock)
            {
                _ = _inputHandle ?? throw new ObjectDisposedException(nameof(_inputHandle));

                if (!MemoryMarshal.TryGetArray(audioData, out var segment))
                    throw new ArgumentException("Audio data must be a contiguous array", nameof(audioData));

                var handle = GCHandle.Alloc(segment.Array, GCHandleType.Pinned);
                var addr = handle.AddrOfPinnedObject() + segment.Offset * sizeof(short);
                var context = CallbackHelper.SimpleCallbackContext.Rent(handle);

                try
                {
                    var contextHandle = CallbackHelper.CreateSendPtr(context);
                    using var runtime = RuntimeWrapper.GetScope();

                    unsafe
                    {
                        NativeMethods.unienc_audio_encoder_push(
                            runtime.Runtime,
                            _inputHandle.DangerousGetHandle(),
                            (nint)addr,
                            (nuint)segment.Count,
                            timestampInSamples,
                            CallbackHelper.GetSimpleCallbackPtr(),
                            contextHandle);
                    }
                }
                catch
                {
                    context.Return();
                    throw;
                }

                return context.Task;
            }
        }

        /// <summary>
        ///     Pulls an encoded audio frame from the encoder.
        /// </summary>
        /// <returns>The encoded frame, or null if no frames are available</returns>
        public ValueTask<EncodedFrame> PullFrameAsync()
        {
            lock (_lock)
            {
                _ = _outputHandle ?? throw new ObjectDisposedException(nameof(_outputHandle));

                var context = CallbackHelper.DataCallbackContext.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);
                using var runtime = RuntimeWrapper.GetScope();

                unsafe
                {
                    NativeMethods.unienc_audio_encoder_pull(
                        runtime.Runtime,
                        _outputHandle.DangerousGetHandle(),
                        CallbackHelper.GetDataCallbackPtr(),
                        contextHandle);
                }

                return context.Task;
            }
        }

        /// <summary>
        ///     Completes the encoding by disposing the input handle.
        ///     This signals that no more samples will be pushed.
        ///     The output handle remains valid to pull remaining encoded frames.
        /// </summary>
        public void CompleteInput()
        {
            lock (_lock)
            {
                var input = _inputHandle;
                _inputHandle = null;
                input?.Dispose();
            }
        }

        private void Dispose(bool disposing)
        {
            lock (_lock)
            {
                var input = _inputHandle;
                _inputHandle = null;
                input?.Dispose();

                var output = _outputHandle;
                _outputHandle = null;
                output?.Dispose();
            }
        }

        ~AudioEncoder()
        {
            Dispose(false);
        }

        private class InputHandle : GeneralHandle
        {
            public InputHandle(IntPtr handle) : base(handle)
            {
            }

            protected override bool ReleaseHandle()
            {
                NativeMethods.unienc_free_audio_encoder_input((nint)handle);
                return true;
            }
        }

        private class OutputHandle : GeneralHandle
        {
            public OutputHandle(IntPtr handle) : base(handle)
            {
            }

            protected override bool ReleaseHandle()
            {
                NativeMethods.unienc_free_audio_encoder_output((nint)handle);
                return true;
            }
        }
    }
}
