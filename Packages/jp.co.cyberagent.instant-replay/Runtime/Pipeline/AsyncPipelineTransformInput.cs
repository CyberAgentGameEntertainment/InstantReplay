// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal class AsyncPipelineTransformInput<TIn, TOut> : IAsyncPipelineInput<TIn>
    {
        private readonly IAsyncPipelineInput<TOut> _next;
        private readonly IPipelineTransform<TIn, TOut> _pipelineTransform;

        public AsyncPipelineTransformInput(IPipelineTransform<TIn, TOut> pipelineTransform,
            IAsyncPipelineInput<TOut> next)
        {
            _pipelineTransform = pipelineTransform;
            _next = next;
        }

        public ValueTask PushAsync(TIn value)
        {
            if (!_pipelineTransform.Transform(value, out var output)) return default;
            return _next.PushAsync(output);
        }

        public ValueTask CompleteAsync(Exception exception = null)
        {
            return _next.CompleteAsync(exception);
        }

        public void Dispose()
        {
            _pipelineTransform?.Dispose();
            _next?.Dispose();
        }
    }
}
