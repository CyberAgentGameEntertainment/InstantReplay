// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace UniEnc
{
    internal unsafe class RuntimeWrapper
    {
        public static readonly RuntimeWrapper Instance = new();

        public readonly Runtime* Runtime = NativeMethods.unienc_new_runtime();

        ~RuntimeWrapper()
        {
            NativeMethods.unienc_drop_runtime(Runtime);
        }
    }
}
