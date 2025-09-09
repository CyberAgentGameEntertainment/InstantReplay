// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace UniEnc
{
    internal unsafe class RuntimeWrapper : SafeHandle
    {
        // A handle to access UniEnc runtime (mainly for Rust async runtime management).
        // We need to drop the async runtime when domain unloads because Unity will crash if async callbacks are
        // invoked through unmanaged function pointer acquired with Marshal.GetFunctionPointerForDelegate() after domain is unloaded.
        // tokio::runtime::Runtime waits for all pending tasks to complete synchronously when dropped.
        // In addition, we can only drop the runtime from the finalizer of THIS object because tokio::runtime::Runtime panics if it is dropped within async context.
        // For example if we keep lifetime of the runtime object by reference counting and release it in Dispose() of other native handles, and it happens to be called from an async callback, it will crash.
        // It means we cannot use the runtime to drop native resources.

        private static RuntimeWrapper _instance = new((nint)NativeMethods.unienc_new_runtime());

        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

        private RuntimeWrapper(IntPtr ptr) : base(IntPtr.Zero, true)
        {
            SetHandle(ptr);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            // wait for all native invocations on other threads depending on the runtime to complete
            // we use ReaderWriteLockSlim to allow USING the runtime from multiple threads simultaneously but forbid DISPOSING it while in use 
            _lock.EnterWriteLock();
            try
            {
                NativeMethods.unienc_drop_runtime((Runtime*)handle);
                SetHandleAsInvalid();
                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        ///     A scope to access UniEnc runtime only for on-stack and short duration.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="ObjectDisposedException"></exception>
        public static Scope GetScope()
        {
            return new Scope(_instance ?? throw new ObjectDisposedException(nameof(_instance)));
        }

        public readonly ref struct Scope
        {
            private readonly RuntimeWrapper _instance;
            public Runtime* Runtime => (Runtime*)_instance.handle;

            public Scope(RuntimeWrapper instance)
            {
                (_instance = instance)._lock.EnterReadLock();
                if (_instance.IsInvalid)
                {
                    _instance._lock.ExitReadLock();
                    throw new InvalidOperationException("Runtime has been disposed already");
                }
            }

            public void Dispose()
            {
                _instance._lock.ExitReadLock();
            }
        }
    }
}
