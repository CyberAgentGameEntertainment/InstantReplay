// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal static class PipelineExtensions
    {
        public static IPipelineInput<TIn> AsInput<TIn, TOut>(this IPipelineTransform<TIn, TOut> transform,
            IPipelineInput<TOut> next)
        {
            return new PipelineTransformInput<TIn, TOut>(transform, next);
        }

        public static IBlockingPipelineInput<TIn> AsBlockingInput<TIn, TOut>(
            this IPipelineTransform<TIn, TOut> transform, IBlockingPipelineInput<TOut> next)
        {
            return new BlockingPipelineTransformInput<TIn, TOut>(transform, next);
        }

        public static IPipelineInput<T> AsNonBlocking<T>(this IBlockingPipelineInput<T> source)
        {
            return new NonBlockingPipelineInput<T>(source);
        }

        private class NonBlockingPipelineInput<T> : IPipelineInput<T>
        {
            private readonly IBlockingPipelineInput<T> _source;

            public NonBlockingPipelineInput(IBlockingPipelineInput<T> source)
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
