using UnityEngine;

namespace InstantReplay
{
    public class BuiltinCameraFrameProvider : IFrameProvider
    {
        private readonly IFrameProvider.ProvideFrame _received;
        private readonly BuiltinCameraFrameProviderReceiver _receiver;

        public BuiltinCameraFrameProvider(Camera camera)
        {
            _receiver = camera.gameObject.AddComponent<BuiltinCameraFrameProviderReceiver>();
            _receiver.OnFrameReceived += _received = frame => { OnFrameProvided?.Invoke(frame); };
        }

        public void Dispose()
        {
            _receiver.OnFrameReceived -= _received;
            if (_receiver && Application.isPlaying) Object.Destroy(_receiver);
        }

        public event IFrameProvider.ProvideFrame OnFrameProvided;
    }
}
