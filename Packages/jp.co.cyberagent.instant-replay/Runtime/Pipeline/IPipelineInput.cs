// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;

namespace InstantReplay
{
    /// <summary>
    ///     Represents single pipeline element mainly for use in data flow without back-pressure.
    ///     <seealso cref="IAsyncPipelineInput{T}" />
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal interface IPipelineInput<in T> : IDisposable
    {
        /// <summary>
        ///     Whether the input will accept a new value now.
        /// </summary>
        /// <returns></returns>
        bool WillAccept();

        /// <summary>
        ///     Pushes a value to the input. This should be completed immediately.
        /// </summary>
        /// <param name="value"></param>
        void Push(T value);

        /// <summary>
        ///     Marks the input as completed and waits for subsequent processing to finish.
        /// </summary>
        /// <param name="exception"></param>
        /// <returns></returns>
        ValueTask CompleteAsync(Exception exception = null);
    }
}
