// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.InteropServices;
using System.Threading;
using UniEnc.Native;

namespace UniEnc
{
    public unsafe class RuntimeWrapper : SafeHandle
    {
        // A handle to the native runtime, which owns the thread pool that runs the asynchronous operations of the
        // encoders and the muxers. A single instance is shared by the whole process.
        //
        // The native handles that require the runtime to be released (the inputs and the outputs of the encoders)
        // keep a reference to this handle with DangerousAddRef(), so the runtime is released only after all of them.
        // ReleaseHandle() takes the write lock, so the runtime is not released while a native function is using it
        // through GetScope().
        //
        // Releasing the runtime does not wait for the tasks in progress. The worker threads exit after every task
        // spawned on the pool has been dropped.

        private static readonly RuntimeWrapper Instance = Create();

#if ENABLE_IL2CPP
        // Maximum time to wait for the native worker threads to detach from IL2CPP at shutdown.
        private const uint ShutdownTimeoutMilliseconds = 1000;
#endif

        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

        private RuntimeWrapper(IntPtr ptr) : base(IntPtr.Zero, true)
        {
            SetHandle(ptr);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        private static RuntimeWrapper Create()
        {
            var instance = new RuntimeWrapper((nint)NativeMethods.unienc_new_runtime());
#if ENABLE_IL2CPP
            // IL2CPP attaches a native thread for each managed callback and keeps a record of every
            // attachment until the finalizer of the managed thread object runs, which makes the shutdown
            // of the application wait for each record. The native worker threads therefore stay
            // attached, and they must be detached before IL2CPP aborts the attached threads at shutdown.
            // AppDomain.ProcessExit is raised before that.
            NativeMethods.unienc_enable_il2cpp_thread_attach();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => instance.ShutdownManagedCallbacks();
#endif
            return instance;
        }

#if ENABLE_IL2CPP
        private void ShutdownManagedCallbacks()
        {
            _lock.EnterReadLock();
            try
            {
                if (IsInvalid)
                    return;

                NativeMethods.unienc_runtime_shutdown_managed_callbacks((Runtime*)handle,
                    ShutdownTimeoutMilliseconds);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
#endif

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

        public static void Tick()
        {
            using var scope = GetScope();
            NativeMethods.unienc_tick_runtime(scope.Runtime);
        }

        /// <summary>
        ///     A scope to access UniEnc runtime only for on-stack and short duration.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="ObjectDisposedException"></exception>
        internal static Scope GetScope()
        {
            return new Scope(Instance ?? throw new ObjectDisposedException(nameof(Instance)));
        }

        internal static Utils.SafeHandleScope GetReferenceScope()
        {
            return Instance?.GetScope() ?? throw new ObjectDisposedException(nameof(Instance));
        }

        internal readonly ref struct Scope
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
