// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal interface IBlockingPipelineInput<in T> : IDisposable
    {
        void Push(T value);
        ValueTask CompleteAsync(Exception exception = null);
    }
}
