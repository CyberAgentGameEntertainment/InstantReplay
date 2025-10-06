// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    internal class MuxerAudioInput : IPipelineInput<EncodedFrame>
    {
        private readonly Muxer _muxer;

        public MuxerAudioInput(Muxer muxer)
        {
            _muxer = muxer;
        }

        public ValueTask PushAsync(EncodedFrame value)
        {
            return _muxer.PushAudioDataAsync(value);
        }

        public ValueTask CompleteAsync(Exception exception = null)
        {
            return _muxer.FinishAudioAsync();
        }

        public void Dispose()
        {
        }
    }
}
