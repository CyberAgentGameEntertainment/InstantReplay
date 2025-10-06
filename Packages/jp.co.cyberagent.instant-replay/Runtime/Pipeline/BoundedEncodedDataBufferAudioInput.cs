// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    internal class BoundedEncodedDataBufferAudioInput : IBlockingPipelineInput<EncodedFrame>
    {
        private readonly BoundedEncodedFrameBuffer _buffer;

        internal BoundedEncodedDataBufferAudioInput(BoundedEncodedFrameBuffer buffer)
        {
            _buffer = buffer;
        }

        public void Push(EncodedFrame value)
        {
            if (!_buffer.TryAddAudioFrame(value))
            {
                value.Dispose();
            }
        }

        public ValueTask CompleteAsync(Exception exception = null)
        {
            return default;
        }

        public void Dispose()
        {
        }
    }
}
