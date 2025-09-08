// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Runtime.InteropServices;

namespace UniEnc
{
    internal abstract class GeneralHandle : SafeHandle
    {
        protected GeneralHandle(IntPtr handle) : base(IntPtr.Zero, true)
        {
            SetHandle(handle);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;
    }
}
