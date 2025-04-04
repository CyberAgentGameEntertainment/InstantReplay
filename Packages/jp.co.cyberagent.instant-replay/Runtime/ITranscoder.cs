// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal interface ITranscoder : IAsyncDisposable
    {
        ValueTask PushFrameAsync(string path, double timestamp, CancellationToken ct = default);

        ValueTask PushAudioSamplesAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);

        ValueTask CompleteAsync();
    }
}
