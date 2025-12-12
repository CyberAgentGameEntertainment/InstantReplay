using System;
using System.Threading.Tasks;
using UniEnc.Native;

namespace UniEnc
{
    /// <summary>
    ///     Multiplexes encoded video and audio streams into a container format.
    /// </summary>
    public sealed class Muxer : IDisposable
    {
        private readonly object _lock = new();
        private AudioInputHandle _audioInputHandle;
        private CompletionHandle _completionHandle;
        private VideoInputHandle _videoInputHandle;

        internal Muxer(nint videoInputHandle, nint audioInputHandle, nint completionHandle)
        {
            _videoInputHandle = new VideoInputHandle(videoInputHandle);
            _audioInputHandle = new AudioInputHandle(audioInputHandle);
            _completionHandle = new CompletionHandle(completionHandle);
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
            lock (_lock)
            {
                _ = _videoInputHandle ?? throw new ObjectDisposedException(nameof(_videoInputHandle));

                var context = CallbackHelper.SimpleCallbackContext.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);
                var data = frame.Data;

                unsafe
                {
                    fixed (byte* dataPtr = data)
                    {
                        using var runtime = RuntimeWrapper.GetScope();

                        NativeMethods.unienc_muxer_push_video(
                            runtime.Runtime,
                            _videoInputHandle.DangerousGetHandle(),
                            (nint)dataPtr,
                            (nuint)data.Length,
                            frame.Timestamp,
                            CallbackHelper.GetSimpleCallbackPtr(),
                            contextHandle);
                    }
                }

                return context.Task;
            }
        }

        /// <summary>
        ///     Pushes encoded audio data to the muxer.
        /// </summary>
        /// <param name="frame">Encoded audio data from the audio encoder</param>
        public ValueTask PushAudioDataAsync(EncodedFrame frame)
        {
            lock (_lock)
            {
                _ = _audioInputHandle ?? throw new ObjectDisposedException(nameof(_audioInputHandle));

                var context = CallbackHelper.SimpleCallbackContext.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);
                var data = frame.Data;

                unsafe
                {
                    fixed (byte* dataPtr = data)
                    {
                        using var runtime = RuntimeWrapper.GetScope();

                        NativeMethods.unienc_muxer_push_audio(
                            runtime.Runtime,
                            _audioInputHandle.DangerousGetHandle(),
                            (nint)dataPtr,
                            (nuint)data.Length,
                            frame.Timestamp,
                            CallbackHelper.GetSimpleCallbackPtr(),
                            contextHandle);
                    }
                }

                return context.Task;
            }
        }

        /// <summary>
        ///     Signals that no more video data will be pushed.
        /// </summary>
        public ValueTask FinishVideoAsync()
        {
            lock (_lock)
            {
                _ = _videoInputHandle ?? throw new ObjectDisposedException(nameof(_videoInputHandle));

                var context = CallbackHelper.SimpleCallbackContext.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);

                using var runtime = RuntimeWrapper.GetScope();

                unsafe
                {
                    NativeMethods.unienc_muxer_finish_video(
                        runtime.Runtime,
                        _videoInputHandle.DangerousGetHandle(),
                        CallbackHelper.GetSimpleCallbackPtr(),
                        contextHandle);
                }

                return context.Task;
            }
        }

        /// <summary>
        ///     Signals that no more audio data will be pushed.
        /// </summary>
        public ValueTask FinishAudioAsync()
        {
            lock (_lock)
            {
                _ = _audioInputHandle ?? throw new ObjectDisposedException(nameof(_audioInputHandle));

                var context = CallbackHelper.SimpleCallbackContext.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);
                using var runtime = RuntimeWrapper.GetScope();

                unsafe
                {
                    NativeMethods.unienc_muxer_finish_audio(
                        runtime.Runtime,
                        _audioInputHandle.DangerousGetHandle(),
                        CallbackHelper.GetSimpleCallbackPtr(),
                        contextHandle);
                }

                return context.Task;
            }
        }

        /// <summary>
        ///     Completes the muxing process and finalizes the output file.
        /// </summary>
        public ValueTask CompleteAsync()
        {
            lock (_lock)
            {
                _ = _completionHandle ?? throw new ObjectDisposedException(nameof(_completionHandle));

                var context = CallbackHelper.SimpleCallbackContext.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);
                using var runtime = RuntimeWrapper.GetScope();

                unsafe
                {
                    NativeMethods.unienc_muxer_complete(
                        runtime.Runtime,
                        _completionHandle.DangerousGetHandle(),
                        CallbackHelper.GetSimpleCallbackPtr(),
                        contextHandle);
                }

                return context.Task;
            }
        }

        private void Dispose(bool disposing)
        {
            lock (_lock)
            {
                var videoInput = _videoInputHandle;
                _videoInputHandle = null;
                videoInput?.Dispose();

                var audioInput = _audioInputHandle;
                _audioInputHandle = null;
                audioInput?.Dispose();

                var completion = _completionHandle;
                _completionHandle = null;
                completion?.Dispose();
            }
        }

        ~Muxer()
        {
            Dispose(false);
        }

        private class VideoInputHandle : GeneralHandle
        {
            public VideoInputHandle(IntPtr handle) : base(handle)
            {
            }

            protected override bool ReleaseHandle()
            {
                NativeMethods.unienc_free_muxer_video_input(handle);
                return true;
            }
        }

        private class AudioInputHandle : GeneralHandle
        {
            public AudioInputHandle(IntPtr handle) : base(handle)
            {
            }

            protected override bool ReleaseHandle()
            {
                NativeMethods.unienc_free_muxer_audio_input(handle);
                return true;
            }
        }


        private class CompletionHandle : GeneralHandle
        {
            public CompletionHandle(IntPtr handle) : base(handle)
            {
            }

            protected override bool ReleaseHandle()
            {
                NativeMethods.unienc_free_muxer_completion_handle(handle);
                return true;
            }
        }
    }
}
