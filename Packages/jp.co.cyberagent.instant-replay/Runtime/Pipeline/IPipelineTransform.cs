// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;

namespace InstantReplay
{
    internal interface IPipelineTransform<in TIn, TOut> : IDisposable
    {
        bool Transform(TIn input, out TOut output);
    }
}
