# UniEnc

Platform abstraction layer for video and audio encoding and muxing for Unity and .NET. It is part
of [Instant Replay for Unity](https://github.com/CyberAgentGameEntertainment/InstantReplay).

## Supported Platforms

See [Instant Replay for Unity](https://github.com/CyberAgentGameEntertainment/InstantReplay?tab=readme-ov-file#requirements).

## Installation

### .NET

.NET 5 or higher is required.

NuGet package: https://www.nuget.org/packages/UniEnc/

### Unity

Provided as a part of [Instant Replay for Unity](https://github.com/CyberAgentGameEntertainment/InstantReplay).

## Usage

See [Example](https://github.com/CyberAgentGameEntertainment/InstantReplay/blob/main/InstantReplay.Externals/src/UniEnc.Example/Program.cs) for more complete example.

### Initialization

```csharp
using var encodingSystem = new EncodingSystem(new VideoEncoderOptions
    {
        Width = width,
        Height = height,
        FpsHint = framerate,
        Bitrate = 2500000 // 2.5Mbps
    },
    new AudioEncoderOptions
    {
        Channels = channels,
        SampleRate = sampleRate,
        Bitrate = 128000 // 128Kbps
    });
```

### Video Encoding

```csharp
const int frameBufferSize = width * height * 4; // input is BGRA32

using var videoEncoder = encodingSystem.CreateVideoEncoder();

async ValueTask ProduceVideoAsync()
{
    using var pool = new SharedBufferPool(frameBufferSize * 4); // rotating 4 buffers

    for (var i = 0; i < framerate * seconds; i++)
    {
        var timestamp = (double)i / framerate;
        SharedBuffer<SpanWrapper> buffer;
        while (!pool.TryAlloc(frameBufferSize, out buffer))
        {
            Thread.Yield();
        }

        using (buffer)
        {
            // Fill buffer with dummy data
            Random.Shared.NextBytes(buffer.Value.UnsafeGetSpan());

            await videoEncoder.PushFrameAsync(buffer, width, height, timestamp);
        }
    }

    videoEncoder.CompleteInput();
}
```

### Audio Encoding

```csharp
using var audioEncoder = encodingSystem.CreateAudioEncoder();

async ValueTask ProduceAudioAsync()
{
    var buffer = ArrayPool<short>.Shared.Rent(1024);
    try
    {
        var totalSamples = (int)Math.Ceiling(seconds * sampleRate);
        for (var i = 0; i < totalSamples; i++)
        {
            var l = (totalSamples - i) * channels;
            var b = buffer.AsMemory(0, Math.Min(buffer.Length, l));

            // 440Hz
            for (var j = 0; j < b.Length / channels; j++)
            {
                var t = (double)(i + j) / sampleRate;
                var sampleValue = (short)(Math.Sin(2.0 * Math.PI * 440.0 * t) * short.MaxValue);
                for (var c = 0; c < channels; c++)
                {
                    b.Span[j * channels + c] = sampleValue; // mono
                }
            }

            await audioEncoder.PushSamplesAsync(b, (ulong)i);
            i += b.Length / channels;
        }
    }
    finally
    {
        ArrayPool<short>.Shared.Return(buffer);
    }

    audioEncoder.CompleteInput();
}
```

### Muxing

```csharp
using var muxer = encodingSystem.CreateMuxer("out.mp4");

async ValueTask TransferAsync()
{
    await Task.WhenAll(TransferVideoSamplesAsync().AsTask(), TransferAudioSamplesAsync().AsTask());
    await muxer.CompleteAsync();
}

async ValueTask TransferVideoSamplesAsync()
{
    do
    {
        var data = await videoEncoder.PullFrameAsync();
        if (data.Data.IsEmpty) break;
        await muxer.PushVideoDataAsync(data);
    } while (true);

    await muxer.FinishVideoAsync();
}

async ValueTask TransferAudioSamplesAsync()
{
    do
    {
        var data = await audioEncoder.PullFrameAsync();
        if (data.Data.IsEmpty) break;
        await muxer.PushAudioDataAsync(data);
    } while (true);

    await muxer.FinishAudioAsync();
}
```

### Organization

```csharp
await Task.WhenAll(
    ProduceVideoAsync().AsTask(),
    ProduceAudioAsync().AsTask(),
    TransferAsync().AsTask());
```
