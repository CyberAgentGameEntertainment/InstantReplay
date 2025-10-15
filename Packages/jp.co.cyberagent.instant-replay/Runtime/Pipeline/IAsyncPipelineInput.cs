// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace InstantReplay
{
    /// <summary>
    ///     Represents single pipeline element mainly for use in data flow where back-pressure occurs.
    ///     <seealso cref="IPipelineInput{T}" />
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal interface IAsyncPipelineInput<in T> : IDisposable
    {
        /// <summary>
        ///     Pushes a value to the input. This should be completed immediately.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        ValueTask PushAsync(T value);

        /// <summary>
        ///     Marks the input as completed and waits for subsequent processing to finish.
        /// </summary>
        /// <param name="exception"></param>
        /// <returns></returns>
        ValueTask CompleteAsync(Exception exception = null);
    }
}
