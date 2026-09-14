// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    internal class EncodedFrameBufferVideoInput : IPipelineInput<EncodedFrame>
    {
        private readonly IEncodedFrameBuffer _buffer;

        internal EncodedFrameBufferVideoInput(IEncodedFrameBuffer buffer)
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
