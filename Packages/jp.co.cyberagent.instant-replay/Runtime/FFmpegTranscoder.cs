// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal class FFmpegTranscoder : ITranscoder
    {
        private readonly FFmpegHost _audioEncoder;
        private readonly Task _audioEncoderTask;
        private readonly string _audioFilename;
        private readonly string _outputFilename;
        private readonly FFmpegHost _videoEncoder;
        private readonly Task _videoEncoderTask;
        private readonly string _videoFilename;
        private double? _lastTimestamp;

        public FFmpegTranscoder(int channels, int sampleRate, string outputFilename)
        {
            _outputFilename = outputFilename;
            _videoFilename = $"{outputFilename}.video.mp4";
            _videoEncoder = new FFmpegHost(
                $"-y -protocol_whitelist \"file,pipe,crypto,data\" -f concat -safe 0 -i pipe:0 -loglevel error \"{_videoFilename}\"",
                true);
            _videoEncoderTask = _videoEncoder.RunAsync().AsTask();
            _videoEncoder.StandardInput.WriteLine("ffconcat version 1.0");

            _audioFilename = $"{outputFilename}.audio.mp4";
            _audioEncoder = new FFmpegHost(
                $"-y -f s16le -ar {sampleRate} -ac {channels} -loglevel error -i pipe:0 -ar {sampleRate} -ac {channels} \"{_audioFilename}\"",
                true);
            _audioEncoderTask = _audioEncoder.RunAsync().AsTask();
        }

        public async ValueTask PushFrameAsync(string path, double timestamp, CancellationToken ct = default)
        {
            if (_lastTimestamp.HasValue)
            {
                var duration = timestamp - _lastTimestamp.Value;
                if (duration < 0)
                    throw new ArgumentException("Timestamp must be greater than the last timestamp.");

                await _videoEncoder.StandardInput.WriteLineAsync($"duration {timestamp - (_lastTimestamp ?? 0)}");
            }

            await _videoEncoder.StandardInput.WriteLineAsync($"file 'file:{path}'");
        }

        public async ValueTask PushAudioSamplesAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var stream = _audioEncoder.StandardInput.BaseStream;
            await stream.WriteAsync(buffer, ct);
            await stream.FlushAsync(ct);
        }

        public async ValueTask CompleteAsync()
        {
            _videoEncoder.StandardInput.Close();
            _audioEncoder.StandardInput.Close();
            await Task.WhenAll(_videoEncoderTask, _audioEncoderTask);

            using var muxer = new FFmpegHost(
                $"-y -loglevel error -i {_videoFilename} -i {_audioFilename} -c copy \"{_outputFilename}\"",
                false);
            await muxer.RunAsync();
        }

        public ValueTask DisposeAsync()
        {
            _videoEncoder.Dispose();
            _audioEncoder.Dispose();
            return default;
        }
    }
}
