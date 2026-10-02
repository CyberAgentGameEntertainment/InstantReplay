// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    internal class EncodedFrameBufferAudioInput : IPipelineInput<EncodedFrame>
    {
        private readonly IEncodedFrameBuffer _buffer;

        internal EncodedFrameBufferAudioInput(IEncodedFrameBuffer buffer)
        {
            _buffer = buffer;
        }

        public bool WillAccept()
        {
            return true;
        }

        public void Push(EncodedFrame value)
        {
            if (!_buffer.TryAddAudioFrame(value))
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
