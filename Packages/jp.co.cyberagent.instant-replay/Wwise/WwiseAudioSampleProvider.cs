using System;
using System.Buffers;
using System.Threading;

namespace InstantReplay.Wwise
{
    public class WwiseAudioSampleProvider : IAudioSampleProvider
    {
        private readonly ulong _outputDeviceId;
        private readonly Action _updateDelegate;

        private ulong _captureSamples;
        private int _isDisposed;

        public WwiseAudioSampleProvider(ulong? outputDeviceId = null)
        {
            _outputDeviceId = outputDeviceId ??

#if WWISE_2024_OR_LATER
                              AkUnitySoundEngine
#else
                              AkSoundEngine
#endif
                                  .GetOutputID(
#if WWISE_2024_OR_LATER
                                      AkUnitySoundEngine
#else
                                      AkSoundEngine
#endif
                                          .AK_INVALID_UNIQUE_ID, 0);
            if (!
#if WWISE_2024_OR_LATER
                AkUnitySoundEngine
#else
                AkSoundEngine
#endif
                    .IsInitialized())
                throw new InvalidOperationException("Wwise sound engine is not initialized.");

            SampleRate =
#if WWISE_2024_OR_LATER
                AkUnitySoundEngine
#else
                AkSoundEngine
#endif
                    .GetSampleRate();
            using var channelConfig = new AkChannelConfig();
            using var audioSinkCapabilities = new Ak3DAudioSinkCapabilities();

#if WWISE_2024_OR_LATER
            AkUnitySoundEngine
#else
            AkSoundEngine
#endif
                .GetOutputDeviceConfiguration(_outputDeviceId, channelConfig, audioSinkCapabilities);
            Channels = channelConfig.uNumChannels;


#if WWISE_2024_OR_LATER
            AkUnitySoundEngine
#else
            AkSoundEngine
#endif
                .ClearCaptureData();

#if WWISE_2024_OR_LATER
            AkUnitySoundEngine
#else
            AkSoundEngine
#endif
                .StartDeviceCapture(_outputDeviceId);

            PlayerLoopEntryPoint.OnAfterUpdate += _updateDelegate = () =>
            {
                var sampleCount =
#if WWISE_2024_OR_LATER
                    AkUnitySoundEngine
#else
                    AkSoundEngine
#endif
                        .UpdateCaptureSampleCount(_outputDeviceId);

                var array = ArrayPool<float>.Shared.Rent(checked((int)sampleCount));
                try
                {
                    var count =
#if WWISE_2024_OR_LATER
                        AkUnitySoundEngine
#else
                        AkSoundEngine
#endif
                            .GetCaptureSamples(_outputDeviceId, array, (uint)array.Length);

                    var time = (double)_captureSamples / SampleRate;
                    _captureSamples += count / Channels;

                    OnProvideAudioSamples?.Invoke(array.AsSpan(0, checked((int)count)), (int)Channels, (int)SampleRate,
                        time);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(array);
                }
            };
        }

        public uint Channels { get; }
        public uint SampleRate { get; }
        public event IAudioSampleProvider.ProvideAudioSamples OnProvideAudioSamples;

        public void Dispose()
        {
            if (!DisposeCore()) return;
            PlayerLoopEntryPoint.OnAfterUpdate -= _updateDelegate;
        }

        ~WwiseAudioSampleProvider()
        {
            DisposeCore();
        }

        private bool DisposeCore()
        {
            if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0) return false;

#if WWISE_2024_OR_LATER
            AkUnitySoundEngine
#else
            AkSoundEngine
#endif
                .StopDeviceCapture(_outputDeviceId);
            return true;
        }
    }
}
