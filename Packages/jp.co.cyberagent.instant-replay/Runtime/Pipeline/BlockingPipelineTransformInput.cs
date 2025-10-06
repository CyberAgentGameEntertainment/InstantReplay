// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal class BlockingPipelineTransformInput<TIn, TOut> : IBlockingPipelineInput<TIn>
    {
        private readonly IPipelineTransform<TIn, TOut> _pipelineTransform;
        private readonly IBlockingPipelineInput<TOut> _next;

        public BlockingPipelineTransformInput(IPipelineTransform<TIn, TOut> pipelineTransform, IBlockingPipelineInput<TOut> next)
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
