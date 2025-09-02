using System;
using System.Text;
using UniEnc.Internal;

namespace UniEnc
{
    /// <summary>
    ///     Main factory class for creating video/audio encoders and muxers.
    /// </summary>
    public sealed class EncodingSystem : IDisposable
    {
        private readonly object _lock = new();
        private bool _disposed;
        private IntPtr _handle;

        /// <summary>
        ///     Creates a new encoding system with the specified options.
        /// </summary>
        public EncodingSystem(VideoEncoderOptions videoOptions, AudioEncoderOptions audioOptions)
        {
            VideoOptions = videoOptions;
            AudioOptions = audioOptions;

            unsafe
            {
                var videoNative = videoOptions.ToNative();
                var audioNative = audioOptions.ToNative();

                var runtime = RuntimeWrapper.Instance;

                _handle = (IntPtr)NativeMethods.unienc_new_encoding_system(runtime.Runtime, &videoNative, &audioNative);

                if (_handle == IntPtr.Zero)
                    throw new UniEncException(UniencErrorKind.InitializationError, "Failed to create encoding system");
            }
        }

        /// <summary>
        ///     Gets the video encoder options used to create this system.
        /// </summary>
        public VideoEncoderOptions VideoOptions { get; }

        /// <summary>
        ///     Gets the audio encoder options used to create this system.
        /// </summary>
        public AudioEncoderOptions AudioOptions { get; }

        /// <summary>
        ///     Releases all resources used by the encoding system.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Creates a new video encoder.
        /// </summary>
        public VideoEncoder CreateVideoEncoder()
        {
            ThrowIfDisposed();

            unsafe
            {
                Mutex* input = null;
                Mutex* output = null;

                var context = CallbackHelper.SimpleCallbackContext.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);
                var task = context.Task;

                var runtime = RuntimeWrapper.Instance;

                var success = NativeMethods.unienc_new_video_encoder(
                    runtime.Runtime,
                    (void*)_handle,
                    &input,
                    &output,
                    CallbackHelper.GetSimpleCallbackPtr(),
                    contextHandle);

                if (task.IsCompleted)
                    task.GetAwaiter().GetResult(); // throws if there was an error

                if (!success || input == null || output == null)
                    throw new UniEncException(UniencErrorKind.InitializationError, "Failed to create video encoder");

                return new VideoEncoder((IntPtr)input, (IntPtr)output);
            }
        }

        /// <summary>
        ///     Creates a new audio encoder.
        /// </summary>
        public AudioEncoder CreateAudioEncoder()
        {
            ThrowIfDisposed();

            unsafe
            {
                Mutex* input = null;
                Mutex* output = null;

                var context = CallbackHelper.SimpleCallbackContext.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);
                var task = context.Task;

                var runtime = RuntimeWrapper.Instance;

                var success = NativeMethods.unienc_new_audio_encoder(
                    runtime.Runtime,
                    (void*)_handle,
                    &input,
                    &output,
                    CallbackHelper.GetSimpleCallbackPtr(),
                    contextHandle);

                if (task.IsCompleted)
                    task.GetAwaiter().GetResult(); // throws if there was an error

                if (!success || input == null || output == null)
                    throw new UniEncException(UniencErrorKind.InitializationError, "Failed to create audio encoder");

                return new AudioEncoder((IntPtr)input, (IntPtr)output);
            }
        }

        /// <summary>
        ///     Creates a new muxer for combining video and audio streams.
        /// </summary>
        public Muxer CreateMuxer(string outputPath)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));

            unsafe
            {
                Mutex* videoInput = null;
                Mutex* audioInput = null;
                Mutex* completionHandle = null;

                var context = CallbackHelper.SimpleCallbackContext.Rent();
                var contextHandle = CallbackHelper.CreateSendPtr(context);
                var task = context.Task;

                var pathBytes = Encoding.UTF8.GetBytes(outputPath + '\0');
                fixed (byte* pathPtr = pathBytes)
                {
                    var runtime = RuntimeWrapper.Instance;

                    var success = NativeMethods.unienc_new_muxer(
                        runtime.Runtime,
                        (void*)_handle,
                        pathPtr,
                        &videoInput,
                        &audioInput,
                        &completionHandle,
                        CallbackHelper.GetSimpleCallbackPtr(),
                        contextHandle);

                    if (task.IsCompleted)
                        task.GetAwaiter().GetResult(); // throws if there was an error

                    if (!success || videoInput == null || audioInput == null || completionHandle == null)
                        throw new UniEncException(UniencErrorKind.InitializationError, "Failed to create muxer");

                    return new Muxer((IntPtr)videoInput, (IntPtr)audioInput, (IntPtr)completionHandle);
                }
            }
        }

        private void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    if (_handle != IntPtr.Zero)
                    {
                        var runtime = RuntimeWrapper.Instance;
                        unsafe
                        {
                            NativeMethods.unienc_free_encoding_system(runtime.Runtime, (void*)_handle);
                        }

                        _handle = IntPtr.Zero;
                    }

                    _disposed = true;
                }
            }
        }

        ~EncodingSystem()
        {
            Dispose(false);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EncodingSystem));
        }
    }
}
