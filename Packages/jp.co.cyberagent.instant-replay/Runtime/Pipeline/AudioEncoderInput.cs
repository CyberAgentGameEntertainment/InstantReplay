// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;

namespace InstantReplay
{
    internal class AudioEncoderInput : IPipelineInput<AudioFrameData>
    {
        private readonly AudioEncoder _audioEncoder;
        private readonly double _sampleRateInOptions;
        private readonly Task _transferTask;

        internal AudioEncoderInput(AudioEncoder audioEncoder, double sampleRateInOptions, IPipelineInput<EncodedFrame> next)
        {
            _audioEncoder = audioEncoder ?? throw new ArgumentNullException(nameof(audioEncoder));
            _sampleRateInOptions = sampleRateInOptions;
            _transferTask = TransferAsync(next);
        }

        private async Task TransferAsync(IPipelineInput<EncodedFrame> next)
        {
            try
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
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public async ValueTask PushAsync(AudioFrameData value)
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

        public ValueTask CompleteAsync(Exception exception = null)
        {
            _audioEncoder.CompleteInput();
            return new ValueTask(_transferTask);
        }

        public void Dispose()
        {
            _audioEncoder?.Dispose();
        }
    }
}
