// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal class PcmAudioFrameDroppingChannelInput : IPipelineInput<PcmAudioFrame>
    {
        private readonly int _capacitySamples;
        private readonly ChannelWriter<PcmAudioFrame> _inner;
        private readonly IAsyncPipelineInput<PcmAudioFrame> _next;
        private readonly Task _processFramesAsync;
        private int _currentNumSamples;

        internal PcmAudioFrameDroppingChannelInput(int capacitySamples, IAsyncPipelineInput<PcmAudioFrame> next)
        {
            _capacitySamples = capacitySamples;
            _next = next;
            var channel = Channel.CreateUnbounded<PcmAudioFrame>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            });

            _inner = channel.Writer;
            _processFramesAsync = ProcessFramesAsync(channel.Reader);
        }

        public bool WillAccept()
        {
            return _currentNumSamples < _capacitySamples;
        }

        public void Push(PcmAudioFrame value)
        {
            if (_processFramesAsync is { IsCompleted: true, Exception: { } ex })
            {
                try
                {
                    value.Dispose();
                }
                catch (Exception ex1)
                {
                    ILogger.LogExceptionCore(ex1);
                }

                throw ex;
            }

            if (_currentNumSamples + value.Data.Length > _capacitySamples || !_inner.TryWrite(value))
            {
                ILogger.LogWarningCore("Dropped audio frame due to full queue.");
                value.Dispose();
            }
            else
            {
                Interlocked.Add(ref _currentNumSamples, value.Data.Length);
            }
        }

        public async ValueTask CompleteAsync(Exception exception = null)
        {
            _inner.TryComplete(exception);
            await _processFramesAsync;
        }

        public void Dispose()
        {
            try
            {
                throw new OperationCanceledException();
            }
            catch (Exception ex)
            {
                _inner.TryComplete(ex);
            }

            _next?.Dispose();
        }

        private async Task ProcessFramesAsync(ChannelReader<PcmAudioFrame> reader)
        {
            try
            {
                await foreach (var value in reader.ReadAllAsync().ConfigureAwait(false))
                {
                    Interlocked.Add(ref _currentNumSamples, -value.Data.Length);
                    await _next.PushAsync(value).ConfigureAwait(false);
                }
            }
            finally
            {
                await _next.CompleteAsync();
            }
        }
    }
}
