// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UniEnc;
using UnityEngine;
using UnityEngine.Rendering;

namespace InstantReplay
{
    internal class NativeBlitTransform : IPipelineTransform<IFrameProvider.Frame, LazyVideoFrameData>
    {
        private readonly uint _destHeight;
        private readonly uint _destWidth;
        private readonly EncodingSystem _encodingSystem;
        private CommandBuffer _commandBuffer;

        public NativeBlitTransform(EncodingSystem encodingSystem, uint destWidth, uint destHeight)
        {
            if (!encodingSystem.IsBlitSupported())
                throw new InvalidOperationException("Native blit is not supported in the current environment.");

            _encodingSystem = encodingSystem;
            _destWidth = destWidth;
            _destHeight = destHeight;
        }

        public bool WillAcceptWhenNextWont => false;

        public bool Transform(IFrameProvider.Frame input, out LazyVideoFrameData output, bool willAcceptedByNextInput)
        {
            if (!willAcceptedByNextInput)
            {
                output = default;
                return false;
            }

            _commandBuffer ??= new CommandBuffer();
            _commandBuffer.Clear();

            var task = _encodingSystem.BlitAsync(_commandBuffer, input.Texture, _destWidth, _destHeight,
                !input.DataStartsAtTop);
            Graphics.ExecuteCommandBuffer(_commandBuffer);

            output = new LazyVideoFrameData(task, (int)_destWidth, (int)_destHeight, input.Timestamp);
            return true;
        }

        public void Dispose()
        {
            _commandBuffer?.Dispose();
            _commandBuffer = null;
        }
    }
}
