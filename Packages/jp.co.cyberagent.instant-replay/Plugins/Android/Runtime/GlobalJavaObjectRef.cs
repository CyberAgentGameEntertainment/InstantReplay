#nullable enable

using System;
using UnityEngine;

namespace AndroidBindgen
{
    internal class GlobalJavaObjectRef
    {
        private bool m_disposed;

        protected IntPtr m_jobject;

        public GlobalJavaObjectRef(IntPtr jobject)
        {
            m_jobject = jobject == IntPtr.Zero ? IntPtr.Zero : AndroidJNI.NewGlobalRef(jobject);
        }

        /*
        ~GlobalJavaObjectRef()
        {
            Dispose();
        }
        */

        public static implicit operator IntPtr(GlobalJavaObjectRef obj)
        {
            return obj.m_jobject;
        }

        public void Dispose()
        {
            if (m_disposed)
                return;

            m_disposed = true;

            if (m_jobject != IntPtr.Zero)
                AndroidJNI.DeleteGlobalRef(m_jobject);
        }
    }
}
