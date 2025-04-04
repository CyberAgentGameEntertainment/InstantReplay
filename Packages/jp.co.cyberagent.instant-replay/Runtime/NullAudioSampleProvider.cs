// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace InstantReplay
{
    /// <summary>
    ///     An audio sample provider that does nothing.
    ///     You can use NullAudioSampleProvider.Instance to disable audio recording.
    /// </summary>
    public class NullAudioSampleProvider : IAudioSampleProvider
    {
        private NullAudioSampleProvider()
        {
        }

        public static NullAudioSampleProvider Instance { get; } = new();

        public event IAudioSampleProvider.ProvideAudioSamples OnProvideAudioSamples
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }
}
