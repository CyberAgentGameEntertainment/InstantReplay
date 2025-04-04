// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading;

namespace InstantReplay
{
    internal static class Utils
    {
        public static TDelegate HoldDelegate<TDelegate>(ref TDelegate field, Func<TDelegate> factory)
            where TDelegate : Delegate
        {
            if (field != null) return field;
            var value = factory();
            var original = Interlocked.CompareExchange(ref field, value, null);
            return original ?? value;
        }

        public static TDelegate HoldDelegate<TDelegate, TCtx>(ref TDelegate field, Func<TCtx, TDelegate> factory,
            TCtx context) where TDelegate : Delegate
        {
            if (field != null) return field;
            var value = factory(context);
            var original = Interlocked.CompareExchange(ref field, value, null);
            return original ?? value;
        }
    }
}
