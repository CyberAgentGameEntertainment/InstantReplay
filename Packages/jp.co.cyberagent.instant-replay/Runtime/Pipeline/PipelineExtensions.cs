// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal static class PipelineExtensions
    {
        public static IAsyncPipelineInput<TIn> AsAsyncInput<TIn, TOut>(this IPipelineTransform<TIn, TOut> transform,
            IAsyncPipelineInput<TOut> next)
        {
            return new AsyncPipelineTransformInput<TIn, TOut>(transform, next);
        }

        public static IPipelineInput<TIn> AsInput<TIn, TOut>(
            this IPipelineTransform<TIn, TOut> transform, IPipelineInput<TOut> next)
        {
            return new PipelineTransformInput<TIn, TOut>(transform, next);
        }

        public static IAsyncPipelineInput<T> AsNonBlocking<T>(this IPipelineInput<T> source)
        {
            return new NonBlockingAsyncPipelineInput<T>(source);
        }

        private class NonBlockingAsyncPipelineInput<T> : IAsyncPipelineInput<T>
        {
            private readonly IPipelineInput<T> _source;

            public NonBlockingAsyncPipelineInput(IPipelineInput<T> source)
            {
                _source = source;
            }

            public void Dispose()
            {
                _source?.Dispose();
            }

            public ValueTask PushAsync(T value)
            {
                _source.Push(value);
                return default;
            }

            public ValueTask CompleteAsync(Exception exception = null)
            {
                return _source.CompleteAsync(exception);
            }
        }
    }
}
