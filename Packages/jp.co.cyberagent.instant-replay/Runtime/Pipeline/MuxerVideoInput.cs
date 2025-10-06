// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    internal class MuxerVideoInput : IPipelineInput<EncodedFrame>
    {
        private readonly Muxer _muxer;

        public MuxerVideoInput(Muxer muxer)
        {
            _muxer = muxer;
        }

        public ValueTask PushAsync(EncodedFrame value)
        {
            return _muxer.PushVideoDataAsync(value);
        }

        public ValueTask CompleteAsync(Exception exception = null)
        {
            return _muxer.FinishVideoAsync();
        }

        public void Dispose()
        {
        }
    }
}
