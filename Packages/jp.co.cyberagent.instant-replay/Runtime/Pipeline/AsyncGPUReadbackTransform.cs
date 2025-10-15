// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace InstantReplay
{
    internal class AsyncGPUReadbackTransform : IPipelineTransform<IFrameProvider.Frame, LazyVideoFrameData>
    {
        public bool Transform(IFrameProvider.Frame input, out LazyVideoFrameData output)
        {
            output = new LazyVideoFrameData(FrameReadback.ReadbackFrameAsync(input.Texture), input.Texture.width,
                input.Texture.height, input.Timestamp);
            return true;
        }

        public void Dispose()
        {
        }
    }
}
