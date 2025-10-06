// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading.Tasks;
using UniEnc;

namespace InstantReplay
{
    internal class FramePreprocessorInput : IPipelineTransform<IFrameProvider.Frame, IFrameProvider.Frame>
    {
        private readonly FramePreprocessor _preprocessor;

        public FramePreprocessorInput(FramePreprocessor preprocessor)
        {
            _preprocessor = preprocessor;
        }

        public bool Transform(IFrameProvider.Frame input, out IFrameProvider.Frame output)
        {
            var outTex = _preprocessor.Process(input.Texture, input.NeedFlipVertically);
            output = new IFrameProvider.Frame(outTex, input.Timestamp, false);
            return true;
        }

        public void Dispose()
        {
            _preprocessor?.Dispose();
        }
    }
}
