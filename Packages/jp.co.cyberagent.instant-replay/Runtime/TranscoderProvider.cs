// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace InstantReplay
{
    internal static class TranscoderProvider
    {
        public static ITranscoder Provide(int outputWidth, int outputHeight, int sampleRate, int channels, string outputFilename)
        {
#if UNITY_EDITOR_OSX || (!UNITY_EDITOR && (UNITY_STANDALONE_OSX || UNITY_IOS))
            return new AppleVideoToolboxTranscoder(outputWidth, outputHeight, sampleRate, channels, outputFilename);
#elif !UNITY_EDITOR && UNITY_ANDROID
            return new AndroidMediaCodecTranscoder(outputWidth, outputHeight, channels, sampleRate, outputFilename);
#elif UNITY_EDITOR_WIN || (!UNITY_EDITOR && UNITY_STANDALONE_WIN)
            return new WindowsMediaFoundationTranscoder(outputWidth, outputHeight, sampleRate, channels, outputFilename);
#else
            throw new System.PlatformNotSupportedException();
#endif
        }
    }
}
