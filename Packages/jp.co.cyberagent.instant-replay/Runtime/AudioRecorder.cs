// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace InstantReplay
{
    /// <summary>
    ///     Holds recorded audio samples on memory.
    /// </summary>
    internal class AudioRecorder : IDisposable
    {
        private readonly double _allowedLag;
        private readonly bool _disposeProvider;
        private readonly IAudioSampleProvider _provider;
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;
        private readonly object _writerLock = new();

        private long? _currentSamplePosition;

        /// <summary>
        /// </summary>
        /// <param name="provider">Source of recorded audio samples.</param>
        /// <param name="disposeProvider">Whether this AudioRecorder automatically disposes specified IAudioSampleProvider.</param>
        /// <param name="reader">Recorded samples reader</param>
        /// <param name="allowedLag">
        ///     Allowed lag in seconds. If there is more lag than this value, a blank is inserted or some
        ///     samples are discarded, resulting a noise.
        /// </param>
        public AudioRecorder(IAudioSampleProvider provider, bool disposeProvider, out PipeReader reader,
            double allowedLag = 0.01)
        {
            provider.OnProvideAudioSamples += OnProvideAudioSamples;
            _provider = provider;
            _disposeProvider = disposeProvider;

            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0)); // unbounded
            _writer = pipe.Writer;
            _reader = reader = pipe.Reader;
            _allowedLag = allowedLag;
        }

        /// <summary>
        ///     Determined number of channels.
        ///     If null, it is not determined yet.
        /// </summary>
        public int? NumChannels { get; private set; }

        /// <summary>
        ///     Determined sample rate.
        ///     If null, it is not determined yet.
        /// </summary>
        public int? SampleRate { get; private set; }

        public void Dispose()
        {
            _provider.OnProvideAudioSamples -= OnProvideAudioSamples;

            lock (_writerLock)
            {
                _writer.Complete();
            }

            if (_disposeProvider) _provider?.Dispose();
        }

        private void OnProvideAudioSamples(ReadOnlySpan<float> samples, int channels, int sampleRate,
            double timestamp)
        {
            var numSamples = samples.Length / channels;

            if (!SampleRate.HasValue)
            {
                if (sampleRate != 41000 && sampleRate != 48000)
                    // NOTE: encoder may fail if sample rate is not 41000 nor 48000
                    SampleRate = 48000;
                else
                    SampleRate = sampleRate;
            }

            NumChannels ??= channels;

            var samplePosition = (long)Math.Round(timestamp * SampleRate.Value);
            var currentSamplePosition = _currentSamplePosition ??= samplePosition;
            var lag = samplePosition - currentSamplePosition;

            var numScaledSamples = SampleRate == sampleRate
                ? numSamples
                : (long)Math.Round(numSamples * ((double)SampleRate.Value / sampleRate));
            long blankOrSkip;
            if (Math.Abs(lag) > _allowedLag * SampleRate)
            {
                // if there is too much lag, skip input or insert blank
                blankOrSkip = lag;
            }
            else
            {
                // scale to position
                blankOrSkip = 0;
                numScaledSamples += lag;
            }

            // write samples
            lock (_writerLock)
            {
                var writeLength = (int)((numScaledSamples + blankOrSkip) * channels * sizeof(short));
                var writeBuffer = _writer.GetSpan(writeLength);
                var writeBufferShort = MemoryMarshal.Cast<byte, short>(writeBuffer); // NOTE: is this alignment safe?

                if (blankOrSkip > 0)
                    writeBufferShort[..(int)blankOrSkip].Clear();

                var skip = (int)Math.Max(0, -blankOrSkip);

                for (var i = skip; i < numScaledSamples; i++)
                for (var j = 0; j < NumChannels; j++)
                {
                    // NOTE: should we interpolate samples?
                    var pos = sampleRate == SampleRate
                        ? i
                        : (int)Math.Floor(i * ((double)sampleRate / SampleRate.Value));
                    var sample = samples[pos * channels + j % channels];
                    var scaledSample = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
                    writeBufferShort[i * NumChannels.Value + j] = scaledSample;
                }

                _writer.Advance(writeLength);
                var flushTask = _writer.FlushAsync();
                var flushAwaiter = flushTask.GetAwaiter();
                if (flushAwaiter.IsCompleted)
                    flushAwaiter.GetResult();
                else
                    flushTask.AsTask().Wait();
            }


            _currentSamplePosition = currentSamplePosition + numScaledSamples + blankOrSkip;
        }

        /// <summary>
        ///     Discards older samples than specified duration.
        /// </summary>
        /// <param name="durationToHold">Duration to hold in seconds.</param>
        public void DiscardSamples(double durationToHold)
        {
            if (SampleRate is { } sampleRate && NumChannels is { } numChannels && _reader.TryRead(out var readResult))
            {
                var keepLength = (long)Math.Ceiling(durationToHold * sampleRate) * numChannels * sizeof(short);
                var bufferedLength = readResult.Buffer.Length;
                var discard = Math.Max(bufferedLength - keepLength, 0);
                _reader.AdvanceTo(readResult.Buffer.GetPosition(discard * numChannels * sizeof(short)));
            }
        }
    }
}
