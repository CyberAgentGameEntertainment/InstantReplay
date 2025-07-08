using System;
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
        private bool _disposed;
        private nint _inputHandle;
        private nint _outputHandle;

        internal AudioEncoder(nint inputHandle, nint outputHandle)
        {
            _inputHandle = inputHandle;
            _outputHandle = outputHandle;
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
        /// <param name="sampleCount">Number of samples (per channel)</param>
        /// <param name="timestampInSamples">Timestamp in samples since start</param>
        public ValueTask PushSamplesAsync(byte[] audioData, ulong sampleCount, ulong timestampInSamples)
        {
            return PushSamplesAsync(audioData.AsSpan(), sampleCount, timestampInSamples);
        }

        /// <summary>
        ///     Pushes raw audio samples to the encoder.
        /// </summary>
        /// <param name="audioData">Raw audio samples (PCM int16 signed)</param>
        /// <param name="sampleCount">Number of samples (per channel)</param>
        /// <param name="timestampInSamples">Timestamp in samples since start</param>
        public ValueTask PushSamplesAsync(ReadOnlySpan<byte> audioData, ulong sampleCount, ulong timestampInSamples)
        {
            ThrowIfDisposed();

            var context = CallbackHelper.SimpleCallbackContext.Rent();
            var contextHandle = CallbackHelper.CreateSendPtr(context);

            unsafe
            {
                fixed (byte* dataPtr = audioData)
                {
                    NativeMethods.unienc_audio_encoder_push(
                        _inputHandle,
                        (nint)dataPtr,
                        (nuint)sampleCount,
                        timestampInSamples,
                        CallbackHelper.GetSimpleCallbackPtr(),
                        contextHandle);
                }
            }

            return context.Task;
        }

        /// <summary>
        ///     Pulls an encoded audio frame from the encoder.
        /// </summary>
        /// <returns>The encoded frame, or null if no frames are available</returns>
        public ValueTask<EncodedFrame> PullFrameAsync()
        {
            ThrowIfDisposed();

            var context = CallbackHelper.DataCallbackContext.Rent();
            var contextHandle = CallbackHelper.CreateSendPtr(context);

            unsafe
            {
                NativeMethods.unienc_audio_encoder_pull(
                    _outputHandle,
                    CallbackHelper.GetDataCallbackPtr(),
                    contextHandle);
            }

            return context.Task;
        }

        private void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    if (_inputHandle != 0)
                    {
                        NativeMethods.unienc_free_audio_encoder_input(_inputHandle);
                        _inputHandle = 0;
                    }

                    if (_outputHandle != 0)
                    {
                        NativeMethods.unienc_free_audio_encoder_output(_outputHandle);
                        _outputHandle = 0;
                    }

                    _disposed = true;
                }
            }
        }

        ~AudioEncoder()
        {
            Dispose(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AudioEncoder));
        }
    }
}
