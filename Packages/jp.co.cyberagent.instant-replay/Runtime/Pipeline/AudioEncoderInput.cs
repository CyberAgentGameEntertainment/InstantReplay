// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    internal class AudioEncoderInput : IAsyncPipelineInput<PcmAudioFrame>
    {
        private readonly AudioEncoder _audioEncoder;
        private readonly IAsyncPipelineInput<EncodedFrame> _next;
        private readonly double _sampleRateInOptions;
        private readonly Task _transferTask;

        internal AudioEncoderInput(AudioEncoder audioEncoder, double sampleRateInOptions,
            IAsyncPipelineInput<EncodedFrame> next)
        {
            _audioEncoder = audioEncoder ?? throw new ArgumentNullException(nameof(audioEncoder));
            _sampleRateInOptions = sampleRateInOptions;
            _next = next;
            _transferTask = TransferAsync(next);
        }

        public ValueTask PushAsync(PcmAudioFrame value)
        {
            return ValueTaskUtils.WhenAny(PushCoreAsync(value), new ValueTask(_transferTask));
        }

        public ValueTask CompleteAsync(Exception exception = null)
        {
            _audioEncoder.CompleteInput();
            return new ValueTask(_transferTask);
        }

        public void Dispose()
        {
            _audioEncoder?.Dispose();
            _next.Dispose();
        }

        public async ValueTask PushCoreAsync(PcmAudioFrame value)
        {
            using var _ = value;

            if (value.Data.IsEmpty)
                throw new ArgumentException("Audio data cannot be empty", nameof(value.Data));

            // Push samples to audio encoder
            try
            {
                await _audioEncoder.PushSamplesAsync(value.Data, (ulong)(value.Timestamp * _sampleRateInOptions))
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // ignore
            }
        }

        private async Task TransferAsync(IAsyncPipelineInput<EncodedFrame> next)
        {
            try
            {
                do
                {
                    // Try to pull encoded frame
                    var encodedFrame = await _audioEncoder.PullFrameAsync().ConfigureAwait(false);

                    if (encodedFrame.Data.IsEmpty)
                        // end
                        return;

                    await next.PushAsync(encodedFrame);
                } while (true);
            }
            finally
            {
                await next.CompleteAsync();
            }
        }
    }
}
