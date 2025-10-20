// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;

namespace InstantReplay
{
    internal interface IPipelineTransform<in TIn, TOut> : IDisposable
    {
        /// <summary>
        ///     Indicates whether to accept the input when the next pipeline input is not accepting.
        /// </summary>
        bool WillAcceptWhenNextWont { get; }

        /// <summary>
        ///     Transforms an input to an output.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="output"></param>
        /// <param name="willAcceptedByNextInput">
        ///     Whether the next pipeline input will accept an element to be produced by this
        ///     transform from now.
        /// </param>
        /// <returns></returns>
        bool Transform(TIn input, out TOut output, bool willAcceptedByNextInput);
    }
}
