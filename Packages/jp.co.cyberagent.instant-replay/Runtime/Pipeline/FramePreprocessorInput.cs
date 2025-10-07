// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace InstantReplay
{
    internal class FramePreprocessorInput : IPipelineTransform<IFrameProvider.Frame, IFrameProvider.Frame>
    {
        private readonly bool _outputDataStartsAtTop;
        private readonly FramePreprocessor _preprocessor;

        public FramePreprocessorInput(FramePreprocessor preprocessor, bool outputDataStartsAtTop)
        {
            _preprocessor = preprocessor;
            _outputDataStartsAtTop = outputDataStartsAtTop;
        }

        public bool Transform(IFrameProvider.Frame input, out IFrameProvider.Frame output)
        {
            var outTex = _preprocessor.Process(input.Texture, input.DataStartsAtTop ^ _outputDataStartsAtTop);
            output = new IFrameProvider.Frame(outTex, input.Timestamp, _outputDataStartsAtTop);
            return true;
        }

        public void Dispose()
        {
            _preprocessor?.Dispose();
        }
    }
}
