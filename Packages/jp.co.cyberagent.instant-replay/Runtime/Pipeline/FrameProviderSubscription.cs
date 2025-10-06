// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal class FrameProviderSubscription : IDisposable
    {
        private readonly IFrameProvider _provider;
        private readonly bool _disposeProvider;
        private readonly IBlockingPipelineInput<IFrameProvider.Frame> _next;
        private IFrameProvider.ProvideFrame _delegate;

        public FrameProviderSubscription(IFrameProvider provider, bool disposeProvider,
            IBlockingPipelineInput<IFrameProvider.Frame> next)
        {
            _provider = provider;
            _disposeProvider = disposeProvider;
            _next = next;
            provider.OnFrameProvided += _delegate = frame => next.Push(frame);
        }
        
        private void Unregister()
        {
            var current = Interlocked.Exchange(ref _delegate, null);
            if (current != null)
            {
                _provider.OnFrameProvided -= current;
            }
        }

        public ValueTask CompleteAsync()
        {
            Unregister();
            return _next.CompleteAsync();
        }

        public void Dispose()
        {
            Unregister();

            if (_disposeProvider)
            {
                _provider.Dispose();
            }

            _next?.Dispose();
        }
    }
}
