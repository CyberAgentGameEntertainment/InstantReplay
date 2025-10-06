// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal class AudioSampleProviderSubscription
    {
        private readonly IAudioSampleProvider _provider;
        private readonly bool _disposeProvider;
        private readonly IBlockingPipelineInput<AudioInputData> _next;
        private IAudioSampleProvider.ProvideAudioSamples _delegate;

        public AudioSampleProviderSubscription(IAudioSampleProvider provider, bool disposeProvider,
            IBlockingPipelineInput<AudioInputData> next)
        {
            _provider = provider;
            _disposeProvider = disposeProvider;
            _next = next;
            provider.OnProvideAudioSamples += _delegate = (samples, channels, sampleRate, timestamp) =>
            {
                unsafe
                {
                    fixed (float* samplesPtr = samples)
                    {
                        next.Push(new AudioInputData(samplesPtr, samples.Length, channels, sampleRate, timestamp));   
                    }
                }
            };
        }
        
        private void Unregister()
        {
            var current = Interlocked.Exchange(ref _delegate, null);
            if (current != null)
            {
                _provider.OnProvideAudioSamples -= current;
            }
        }

        public ValueTask CompleteAsync()
        {
            Unregister();
            return _next.CompleteAsync();
        }

        public void Dispose()
        {
            Unregister();

            if (_disposeProvider)
            {
                _provider.Dispose();
            }

            _next?.Dispose();
        }
    }
}
