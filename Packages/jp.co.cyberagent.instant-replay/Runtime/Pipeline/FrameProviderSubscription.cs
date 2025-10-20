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
        private readonly bool _disposeProvider;
        private readonly IPipelineInput<IFrameProvider.Frame> _next;
        private readonly IFrameProvider _provider;
        private IFrameProvider.ProvideFrame _delegate;

        public FrameProviderSubscription(IFrameProvider provider, bool disposeProvider,
            IPipelineInput<IFrameProvider.Frame> next)
        {
            _provider = provider;
            _disposeProvider = disposeProvider;
            _next = next;
            provider.OnFrameProvided += _delegate = frame =>
            {
                if (!next.WillAccept()) return;
                next.Push(frame);
            };
        }

        public void Dispose()
        {
            Unregister();

            if (_disposeProvider)
                _provider.Dispose();

            _next?.Dispose();
        }

        private void Unregister()
        {
            var current = Interlocked.Exchange(ref _delegate, null);
            if (current != null)
                _provider.OnFrameProvided -= current;
        }

        public ValueTask CompleteAsync()
        {
            Unregister();
            return _next.CompleteAsync();
        }
    }
}
