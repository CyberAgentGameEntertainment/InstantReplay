// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal interface IPipelineInput<in T> : IDisposable
    {
        ValueTask PushAsync(T value);
        ValueTask CompleteAsync(Exception exception = null);
    }
}
