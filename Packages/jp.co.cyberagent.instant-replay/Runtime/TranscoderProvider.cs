// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace InstantReplay
{
    internal static class TranscoderProvider
    {
        public static ITranscoder Provide(int outputWidth, int outputHeight, int sampleRate, int channels, string outputFilename)
        {
#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN || (!UNITY_EDITOR && (UNITY_STANDALONE_OSX || UNITY_IOS || UNITY_ANDROID || UNITY_STANDALONE_WIN))
            return new UniEncTranscoder(outputWidth, outputHeight, sampleRate, channels, outputFilename);
#else
            return new FFmpegTranscoder(channels, sampleRate, outputFilename);
#endif
        }
    }
}
