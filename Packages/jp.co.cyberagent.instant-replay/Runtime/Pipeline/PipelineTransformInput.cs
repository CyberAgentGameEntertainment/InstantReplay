// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal class PipelineTransformInput<TIn, TOut> : IPipelineInput<TIn>
    {
        private readonly IPipelineInput<TOut> _next;
        private readonly IPipelineTransform<TIn, TOut> _pipelineTransform;

        public PipelineTransformInput(IPipelineTransform<TIn, TOut> pipelineTransform, IPipelineInput<TOut> next)
        {
            _pipelineTransform = pipelineTransform;
            _next = next;
        }

        public void Push(TIn value)
        {
            if (!_pipelineTransform.Transform(value, out var output)) return;

            _next.Push(output);
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
