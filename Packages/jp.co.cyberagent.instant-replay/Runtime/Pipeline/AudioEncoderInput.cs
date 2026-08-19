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
        private readonly SharedTaskRaceGuard _raceGuard;
        private readonly Task _transferTask;

        internal AudioEncoderInput(AudioEncoder audioEncoder, IAsyncPipelineInput<EncodedFrame> next)
        {
            _audioEncoder = audioEncoder ?? throw new ArgumentNullException(nameof(audioEncoder));
            _next = next;
            _transferTask = TransferAsync(next);
            _raceGuard = new SharedTaskRaceGuard(_transferTask);
        }

        public ValueTask PushAsync(PcmAudioFrame value)
        {
            return _raceGuard.Race(PushCoreAsync(value).AsValueTask());
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

        private async PooledValueTask PushCoreAsync(PcmAudioFrame value)
        {
            using var _ = value;

            if (value.Data.IsEmpty)
                throw new ArgumentException("Audio data cannot be empty", nameof(value.Data));

            // Push samples to audio encoder.
            // Use the integer sample position carried on the frame instead of reconstructing it from the
            // seconds-based Timestamp; `(ulong)(Timestamp * sampleRate)` truncates and intermittently lands
            // one sample early, which the native encoder treats as a one-sample forward gap and fills with
            // silence (audible clicks and audio drift).
            try
            {
                await _audioEncoder.PushSamplesAsync(value.Data, (ulong)value.SamplePosition)
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
