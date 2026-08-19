using System;
using UniEnc.Native;

namespace UniEnc
{
    /// <summary>
    ///     Options for configuring video encoding parameters.
    /// </summary>
    public struct VideoEncoderOptions
    {
        /// <summary>
        ///     Width of the video in pixels.
        /// </summary>
        public uint Width { get; set; }

        /// <summary>
        ///     Height of the video in pixels.
        /// </summary>
        public uint Height { get; set; }

        /// <summary>
        ///     Frames per second hint for the encoder.
        /// </summary>
        public uint FpsHint { get; set; }

        /// <summary>
        ///     Target bitrate in bits per second.
        /// </summary>
        public uint Bitrate { get; set; }

        /// <summary>
        ///     Maximum interval between IDR (key) frames, in seconds. Required, and must be finite and greater than 0.
        ///     Every platform encoder is configured with this value, so there is no "leave it to the platform" case.
        /// </summary>
        public float IdrIntervalSeconds { get; set; }

        /// <summary>
        ///     Validates the options and throws if invalid.
        /// </summary>
        internal void Validate()
        {
            if (Width == 0 || Height == 0)
                throw new ArgumentException("Video width and height must be greater than 0");

            if (FpsHint == 0)
                throw new ArgumentException("FPS hint must be greater than 0");

            if (Bitrate == 0)
                throw new ArgumentException("Bitrate must be greater than 0");

            if (IdrIntervalSeconds <= 0f || float.IsNaN(IdrIntervalSeconds) ||
                float.IsInfinity(IdrIntervalSeconds))
                throw new ArgumentException("IDR interval must be a finite value greater than 0");
        }

        /// <summary>
        ///     Converts to native struct for interop.
        /// </summary>
        internal VideoEncoderOptionsNative ToNative()
        {
            Validate();
            return new VideoEncoderOptionsNative
            {
                width = Width,
                height = Height,
                fps_hint = FpsHint,
                bitrate = Bitrate,
                idr_interval_seconds = IdrIntervalSeconds
            };
        }
    }
}
