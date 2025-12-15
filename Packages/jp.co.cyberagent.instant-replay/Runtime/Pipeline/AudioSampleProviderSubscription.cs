// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal class AudioSampleProviderSubscription
    {
        private readonly bool _disposeProvider;
        private readonly IPipelineInput<InputAudioFrame> _next;
        private readonly IAudioSampleProvider _provider;
        private IAudioSampleProvider.ProvideAudioSamples _delegate;

        public AudioSampleProviderSubscription(IAudioSampleProvider provider, bool disposeProvider,
            Action<Exception> onException, IPipelineInput<InputAudioFrame> next)
        {
            _provider = provider;
            _disposeProvider = disposeProvider;
            _next = next;
            provider.OnProvideAudioSamples += _delegate = (samples, channels, sampleRate, timestamp) =>
            {
                try
                {
                    if (!next.WillAccept()) return;

                    unsafe
                    {
                        fixed (float* samplesPtr = samples)
                        {
                            next.Push(new InputAudioFrame(samplesPtr, samples.Length, channels, sampleRate, timestamp));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Unregister();
                    onException?.Invoke(ex);
                }
            };
        }

        private void Unregister()
        {
            var current = Interlocked.Exchange(ref _delegate, null);
            if (current != null)
                _provider.OnProvideAudioSamples -= current;
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
                _provider.Dispose();

            _next?.Dispose();
        }
    }
}
