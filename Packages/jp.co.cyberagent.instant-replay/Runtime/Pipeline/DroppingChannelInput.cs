// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks;

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
            _processVideoFramesTask = ProcessItemsAsync(channel.Reader);
        }

        public bool WillAccept()
        {
            var waitToWriteAsync = _inner.WaitToWriteAsync();
            if (waitToWriteAsync.IsCompleted)
                return waitToWriteAsync.Result;

            // forget
            var awaiter = waitToWriteAsync.GetAwaiter();
            awaiter.UnsafeOnCompleted(PooledActionOnce<ValueTaskAwaiter<bool>>
                .Get(static awaiter => { awaiter.GetResult(); }, awaiter).Wrapper);
            return false;
        }

        public void Push(T value)
        {
            if (_processVideoFramesTask is { IsCompleted: true, Exception: { } ex })
            {
                try
                {
                    _onDrop?.Invoke(value);
                }
                catch (Exception ex1)
                {
                    ILogger.LogExceptionCore(ex1);
                }

                throw ex;
            }

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

        private async Task ProcessItemsAsync(ChannelReader<T> reader)
        {
            try
            {
                await foreach (var value in reader.ReadAllAsync().ConfigureAwait(false))
                    await _next.PushAsync(value).ConfigureAwait(false);
            }
            finally
            {
                await _next.CompleteAsync();
            }
        }
    }
}
