// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     Holds at most one provider instance, so that providers that may exist only once at a time are never created
    ///     twice. The previous instance is always disposed before a new one is created.
    /// </summary>
    internal sealed class ProviderSlot<T> : IDisposable where T : class, IDisposable
    {
        private readonly Func<T> _defaultFactory;
        private Func<T> _factory;
        private T _instance;
        private bool _owned;

        public ProviderSlot(Func<T> defaultFactory)
        {
            _defaultFactory = defaultFactory;
            _factory = defaultFactory;
        }

        public void Dispose()
        {
            Release();
        }

        /// <summary>
        ///     Returns the current instance, creating one from the factory when there is none.
        /// </summary>
        public T Get()
        {
            if (_instance != null) return _instance;
            _instance = _factory();
            _owned = true;
            return _instance;
        }

        /// <summary>
        ///     Uses <paramref name="factory" /> from now on. Null restores the default factory.
        /// </summary>
        public void SetFactory(Func<T> factory)
        {
            Release();
            _factory = factory ?? _defaultFactory;
        }

        /// <summary>
        ///     Uses an instance supplied by the caller. It is disposed by the slot only when
        ///     <paramref name="takeOwnership" /> is true.
        /// </summary>
        public void SetInstance(T instance, bool takeOwnership)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (ReferenceEquals(instance, _instance))
            {
                _owned = takeOwnership;
                return;
            }

            Release();
            _instance = instance;
            _owned = takeOwnership;
            // The supplied instance cannot be recreated, so a later invalidation falls back to the default factory.
            _factory = _defaultFactory;
        }

        /// <summary>
        ///     Disposes the instance if the slot owns it, so that the next <see cref="Get" /> creates a fresh one. An
        ///     instance owned by the caller is kept.
        /// </summary>
        public void Invalidate()
        {
            if (_instance != null && _owned) Release();
        }

        private void Release()
        {
            var instance = _instance;
            var owned = _owned;
            _instance = null;
            _owned = false;
            if (instance != null && owned) instance.Dispose();
        }
    }
}
