// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace InstantReplay
{
    internal class DirectFrameDataTransform : IPipelineTransform<IFrameProvider.Frame, LazyVideoFrameData>
    {
        public static readonly DirectFrameDataTransform Instance = new();

        public void Dispose()
        {
        }

        public bool WillAcceptWhenNextWont => false;

        public bool Transform(IFrameProvider.Frame input, out LazyVideoFrameData output, bool willAcceptedByNextInput)
        {
            if (!willAcceptedByNextInput)
            {
                output = default;
                return false;
            }

            output = new LazyVideoFrameData(input.Texture, input.Timestamp);
            return true;
        }
    }
}
