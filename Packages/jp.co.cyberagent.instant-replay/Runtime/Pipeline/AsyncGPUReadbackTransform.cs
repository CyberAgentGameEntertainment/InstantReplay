// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using UniEnc;

namespace InstantReplay
{
    internal class AsyncGPUReadbackTransform : IPipelineTransform<IFrameProvider.Frame, LazyVideoFrameData>
    {
        private SharedBufferPool _bufferPool;

        public AsyncGPUReadbackTransform(SharedBufferPool bufferPool)
        {
            _bufferPool = bufferPool;
        }

        public bool WillAcceptWhenNextWont => false;

        public bool Transform(IFrameProvider.Frame input, out LazyVideoFrameData output, bool willAcceptedByNextInput)
        {
            if (!willAcceptedByNextInput)
            {
                output = default;
                return false;
            }

            if (!FrameReadback.TryReadbackFrameAsync(input.Texture, ref _bufferPool, out var task))
            {
                // buffer pool exhausted
                ILogger.LogWarningCore("AsyncGPUReadbackTransform: Buffer pool exhausted.");
                output = default;
                return false;
            }

            output = new LazyVideoFrameData(task, input.Texture.width,
                input.Texture.height, input.Timestamp);
            return true;
        }

        public void Dispose()
        {
            _bufferPool.Dispose();
        }
    }
}
