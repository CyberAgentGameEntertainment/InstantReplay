using System;
using UniEnc.Native;

namespace UniEnc
{
    /// <summary>
    ///     Options for configuring audio encoding parameters.
    /// </summary>
    public struct AudioEncoderOptions
    {
        /// <summary>
        ///     Sample rate in Hz.
        /// </summary>
        public uint SampleRate { get; set; }

        /// <summary>
        ///     Number of audio channels.
        /// </summary>
        public uint Channels { get; set; }

        /// <summary>
        ///     Target bitrate in bits per second.
        /// </summary>
        public uint Bitrate { get; set; }

        /// <summary>
        ///     Validates the options and throws if invalid.
        /// </summary>
        internal void Validate()
        {
            if (SampleRate == 0)
                throw new ArgumentException("Sample rate must be greater than 0");

            if (Channels == 0)
                throw new ArgumentException("Number of channels must be greater than 0");

            if (Bitrate == 0)
                throw new ArgumentException("Bitrate must be greater than 0");
        }

        /// <summary>
        ///     Converts to native struct for interop.
        /// </summary>
        internal AudioEncoderOptionsNative ToNative()
        {
            Validate();
            return new AudioEncoderOptionsNative
            {
                sample_rate = SampleRate,
                channels = Channels,
                bitrate = Bitrate
            };
        }
    }
}
