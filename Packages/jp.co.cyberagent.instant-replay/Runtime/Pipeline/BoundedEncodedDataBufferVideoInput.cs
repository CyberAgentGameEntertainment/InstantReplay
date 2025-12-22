// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    internal class BoundedEncodedDataBufferVideoInput : IPipelineInput<EncodedFrame>
    {
        private readonly BoundedEncodedFrameBuffer _buffer;

        internal BoundedEncodedDataBufferVideoInput(BoundedEncodedFrameBuffer buffer)
        {
            _buffer = buffer;
        }

        public bool WillAccept()
        {
            return true;
        }

        public void Push(EncodedFrame value)
        {
            if (!_buffer.TryAddVideoFrame(value))
                value.Dispose();
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
