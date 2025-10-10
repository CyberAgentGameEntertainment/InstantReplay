// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using UnityEngine;

namespace InstantReplay
{
    internal class DroppingChannelInput<T> : IPipelineInput<T>
    {
        private readonly ChannelWriter<T> _inner;
        private readonly IAsyncPipelineInput<T> _next;
        private readonly Action<T> _onDrop;
        private readonly Task _processVideoFramesTask;

        internal DroppingChannelInput(int capacity, Action<T> onDrop, IAsyncPipelineInput<T> next)
        {
            _onDrop = onDrop;
            _next = next;
            var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true
            });

            _inner = channel.Writer;
            _processVideoFramesTask = ProcessVideoFramesAsync(channel.Reader);
        }

        public void Push(T value)
        {
            if (!_inner.TryWrite(value))
                _onDrop?.Invoke(value);
        }

        public async ValueTask CompleteAsync(Exception exception = null)
        {
            _inner.TryComplete(exception);
            await _processVideoFramesTask;
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

        private async Task ProcessVideoFramesAsync(ChannelReader<T> reader)
        {
            try
            {
                try
                {
                    await foreach (var value in reader.ReadAllAsync().ConfigureAwait(false))
                        await _next.PushAsync(value);
                }
                finally
                {
                    await _next.CompleteAsync();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.LogException(ex);
            }
        }
    }
}
