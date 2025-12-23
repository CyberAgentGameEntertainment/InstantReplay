using System.Buffers;
using UniEnc;

const int width = 1280;
const int height = 720;
const int framerate = 30;
const int frameBufferSize = width * height * 4; // BGRA32
const double seconds = 5.0;
const int sampleRate = 48000;
const int channels = 2;

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

using var videoEncoder = encodingSystem.CreateVideoEncoder();
using var audioEncoder = encodingSystem.CreateAudioEncoder();
using var muxer = encodingSystem.CreateMuxer("out.mp4");

await Task.WhenAll(
    ProduceVideoAsync().AsTask(),
    ProduceAudioAsync().AsTask(),
    TransferAsync().AsTask());

async ValueTask ProduceVideoAsync()
{
    using var pool = new SharedBufferPool(frameBufferSize * 4);

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
