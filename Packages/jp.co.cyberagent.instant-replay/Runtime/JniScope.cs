// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     A disposable scope that attaches and detaches the JNI environment automatically.
    ///     It is ref struct so that it cannot be passed to another thread because the JNI env is attached to the current
    ///     thread.
    /// </summary>
    internal readonly ref struct JniScope
    {
        private readonly bool _detached;

        private JniScope(bool detached)
        {
            _detached = detached;
            if (detached)
            {
                var ret = AndroidJNI.AttachCurrentThread();
                if (ret != 0)
                    throw new Exception($"Failed to attach JNI environment: {ret}");
            }
        }

        public static JniScope Create()
        {
            // HACK: We check if current thread is attached to JNI environment already by the return value of AndroidJNI.FindClass()
            // because AndroidJNI class itself doesn't provide a method to do that.
            var ptr = AndroidJNI.FindClass("android/app/Activity");
            var detached = ptr == (nint)0;
            if (!detached)
                AndroidJNI.DeleteLocalRef(ptr);

            return new JniScope(detached);
        }

        public void Dispose()
        {
            // We should not detach the JNI environment if it's attached originally.
            if (_detached)
            {
                var ret = AndroidJNI.DetachCurrentThread();
                if (ret != 0)
                    throw new Exception($"Failed to detach JNI environment: {ret}");
            }
        }
    }
}
