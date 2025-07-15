using System;
using System.Threading.Tasks;
using UniEnc.Internal;

namespace UniEnc
{
    /// <summary>
    ///     Multiplexes encoded video and audio streams into a container format.
    /// </summary>
    public sealed class Muxer : IDisposable
    {
        private readonly object _lock = new();
        private nint _audioInputHandle;
        private nint _completionHandle;
        private bool _disposed;
        private nint _videoInputHandle;

        internal Muxer(nint videoInputHandle, nint audioInputHandle, nint completionHandle)
        {
            _videoInputHandle = videoInputHandle;
            _audioInputHandle = audioInputHandle;
            _completionHandle = completionHandle;
        }

        /// <summary>
        ///     Releases all resources used by the muxer.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Pushes encoded video data to the muxer.
        /// </summary>
        /// <param name="frame">Encoded video data from the video encoder</param>
        public ValueTask PushVideoDataAsync(in EncodedFrame frame)
        {
            ThrowIfDisposed();

            var context = CallbackHelper.SimpleCallbackContext.Rent();
            var contextHandle = CallbackHelper.CreateSendPtr(context);
            var data = frame.Data;

            unsafe
            {
                fixed (byte* dataPtr = data)
                {
                    NativeMethods.unienc_muxer_push_video(
                        _videoInputHandle,
                        (nint)dataPtr,
                        (nuint)data.Length,
                        frame.Timestamp,
                        CallbackHelper.GetSimpleCallbackPtr(),
                        contextHandle);
                }
            }

            return context.Task;
        }

        /// <summary>
        ///     Pushes encoded audio data to the muxer.
        /// </summary>
        /// <param name="frame">Encoded audio data from the audio encoder</param>
        public ValueTask PushAudioDataAsync(EncodedFrame frame)
        {
            ThrowIfDisposed();

            var context = CallbackHelper.SimpleCallbackContext.Rent();
            var contextHandle = CallbackHelper.CreateSendPtr(context);
            var data = frame.Data;

            unsafe
            {
                fixed (byte* dataPtr = data)
                {
                    NativeMethods.unienc_muxer_push_audio(
                        _audioInputHandle,
                        (nint)dataPtr,
                        (nuint)data.Length,
                        frame.Timestamp,
                        CallbackHelper.GetSimpleCallbackPtr(),
                        contextHandle);
                }
            }

            return context.Task;
        }

        /// <summary>
        ///     Signals that no more video data will be pushed.
        /// </summary>
        public ValueTask FinishVideoAsync()
        {
            ThrowIfDisposed();

            var context = CallbackHelper.SimpleCallbackContext.Rent();
            var contextHandle = CallbackHelper.CreateSendPtr(context);

            unsafe
            {
                NativeMethods.unienc_muxer_finish_video(
                    _videoInputHandle,
                    CallbackHelper.GetSimpleCallbackPtr(),
                    contextHandle);
            }

            return context.Task;
        }

        /// <summary>
        ///     Signals that no more audio data will be pushed.
        /// </summary>
        public ValueTask FinishAudioAsync()
        {
            ThrowIfDisposed();

            var context = CallbackHelper.SimpleCallbackContext.Rent();
            var contextHandle = CallbackHelper.CreateSendPtr(context);

            unsafe
            {
                NativeMethods.unienc_muxer_finish_audio(
                    _audioInputHandle,
                    CallbackHelper.GetSimpleCallbackPtr(),
                    contextHandle);
            }

            return context.Task;
        }

        /// <summary>
        ///     Completes the muxing process and finalizes the output file.
        /// </summary>
        public ValueTask CompleteAsync()
        {
            ThrowIfDisposed();

            var context = CallbackHelper.SimpleCallbackContext.Rent();
            var contextHandle = CallbackHelper.CreateSendPtr(context);

            unsafe
            {
                NativeMethods.unienc_muxer_complete(
                    _completionHandle,
                    CallbackHelper.GetSimpleCallbackPtr(),
                    contextHandle);
            }

            return context.Task;
        }

        private void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_completionHandle != 0)
                {
                    NativeMethods.unienc_free_muxer_completion_handle(_completionHandle);
                    _completionHandle = 0;
                }

                if (_videoInputHandle != 0)
                {
                    NativeMethods.unienc_free_muxer_video_input(_videoInputHandle);
                    _videoInputHandle = 0;
                }

                if (_audioInputHandle != 0)
                {
                    NativeMethods.unienc_free_muxer_audio_input(_audioInputHandle);
                    _audioInputHandle = 0;
                }

                _disposed = true;
            }
        }

        ~Muxer()
        {
            Dispose(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Muxer));
        }
    }
}
