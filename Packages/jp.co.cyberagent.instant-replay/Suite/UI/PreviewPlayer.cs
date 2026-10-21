// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.UIElements;
#if INSTANTREPLAY_VIDEO && !UNITY_WEBGL
using UnityEngine.Video;
#endif

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Plays a recording into an <see cref="Image" /> through a <see cref="VideoPlayer" /> rendering into a
    ///     <see cref="RenderTexture" />. Unavailable on WebGL, where the player cannot open local files, and when the Video
    ///     module is disabled. Capturing a frame additionally requires the Image Conversion module.
    /// </summary>
    internal sealed class PreviewPlayer : IDisposable
    {
#if INSTANTREPLAY_VIDEO && !UNITY_WEBGL
        // A seek that has not completed within this time is treated as completed, in case the player never reports it.
        private const float SeekTimeoutSeconds = 1;

        // A completed seek within this distance of the last requested position is taken to have reached it.
        private const double SeekTolerance = 0.1;

        private readonly GameObject _gameObject;
        private readonly VideoPlayer _player;
        private float _lastSeekRealtime;
        private int _pendingSeeks;
        private double _seekTarget;
        private RenderTexture _texture;

        public PreviewPlayer(Transform parent)
        {
            Image = new Image { scaleMode = ScaleMode.ScaleToFit };
            Image.AddToClassList("irs-preview");

            _gameObject = new GameObject("InstantReplaySuite Preview") { hideFlags = HideFlags.DontSave };
            _gameObject.transform.SetParent(parent, false);
            _player = _gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.isLooping = true;
            _player.source = VideoSource.Url;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.audioOutputMode = VideoAudioOutputMode.Direct;
            _player.prepareCompleted += OnPrepared;
            _player.seekCompleted += OnSeekCompleted;
            _player.errorReceived += (_, message) => Error?.Invoke(message);
        }

        public static bool IsSupported => true;

        public bool IsPlaying => _player != null && _player.isPlaying;

        /// <summary>Whether a frame is being shown.</summary>
        public bool HasFrame => _player != null && _player.isPrepared && _texture != null;

        /// <summary>
        ///     Playback position in seconds. While a seek is in progress, this is the requested position, because on some
        ///     platforms such as iOS and Android the player keeps reporting the previous position until the seek completes.
        /// </summary>
        public double Time
        {
            get
            {
                if (_player == null) return 0;
                return IsSeeking ? _seekTarget : _player.time;
            }
        }

        private bool IsSeeking =>
            _pendingSeeks > 0 && UnityEngine.Time.realtimeSinceStartup - _lastSeekRealtime < SeekTimeoutSeconds;

        public void Seek(double seconds)
        {
            if (_player == null || !_player.isPrepared || !_player.canSetTime) return;
            _seekTarget = Math.Max(0, seconds);
            _pendingSeeks = IsSeeking ? _pendingSeeks + 1 : 1;
            _lastSeekRealtime = UnityEngine.Time.realtimeSinceStartup;
            _player.time = _seekTarget;
        }

        public void Pause()
        {
            if (_player != null && _player.isPlaying) _player.Pause();
        }

        public void Resume()
        {
            if (_player != null && _player.isPrepared && !_player.isPlaying) _player.Play();
        }

        public static bool CanCaptureFrame =>
#if INSTANTREPLAY_IMAGECONVERSION
            true;
#else
            false;
#endif

        /// <summary>
        ///     Encodes the frame being shown as PNG, scaled down so that the longer side does not exceed
        ///     <paramref name="maxLongSide" />. Returns null when there is no frame or the Image Conversion module is
        ///     disabled.
        /// </summary>
        public byte[] CaptureFramePng(int maxLongSide)
        {
#if INSTANTREPLAY_IMAGECONVERSION
            if (!HasFrame) return null;

            var scale = Math.Min(1f, (float)maxLongSide / Math.Max(_texture.width, _texture.height));
            var width = Math.Max(1, (int)Math.Round(_texture.width * scale));
            var height = Math.Max(1, (int)Math.Round(_texture.height * scale));

            var scaled = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var readable = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(_texture, scaled);
                RenderTexture.active = scaled;
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply();
                return readable.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(scaled);
                UnityEngine.Object.Destroy(readable);
            }
#else
            return null;
#endif
        }

        public void Play(string path)
        {
            Stop();
            _player.url = path;
            _player.Prepare();
        }

        public void TogglePause()
        {
            if (string.IsNullOrEmpty(_player.url)) return;
            if (_player.isPlaying) _player.Pause();
            else _player.Play();
        }

        public void Stop()
        {
            if (_player == null) return;
            _pendingSeeks = 0;
            _player.Stop();
            _player.url = null;
            _player.targetTexture = null;
            Image.image = null;
            ReleaseTexture();
        }

        public void Dispose()
        {
            Stop();
            if (_gameObject != null) UnityEngine.Object.Destroy(_gameObject);
        }

        private void OnPrepared(VideoPlayer player)
        {
            var width = (int)Math.Max(2, player.width);
            var height = (int)Math.Max(2, player.height);
            if (_texture == null || _texture.width != width || _texture.height != height)
            {
                ReleaseTexture();
                _texture = new RenderTexture(width, height, 0) { name = "InstantReplaySuite Preview" };
            }

            player.targetTexture = _texture;
            Image.image = _texture;
            player.Play();
        }

        // Some platforms report a single completion for several seeks issued in a row, so reaching the last requested
        // position completes all of them.
        private void OnSeekCompleted(VideoPlayer player)
        {
            if (Math.Abs(player.time - _seekTarget) < SeekTolerance) _pendingSeeks = 0;
            else _pendingSeeks = Math.Max(0, _pendingSeeks - 1);
        }

        private void ReleaseTexture()
        {
            if (_texture == null) return;
            _texture.Release();
            UnityEngine.Object.Destroy(_texture);
            _texture = null;
        }
#else
        public PreviewPlayer(Transform parent)
        {
            Image = new Image();
            Image.AddToClassList("irs-preview");
        }

        public static bool IsSupported => false;

        public bool IsPlaying => false;

        public bool HasFrame => false;

        public double Time => 0;

        public static bool CanCaptureFrame => false;

        public void Seek(double seconds)
        {
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public byte[] CaptureFramePng(int maxLongSide)
        {
            return null;
        }

        public void Play(string path)
        {
        }

        public void TogglePause()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
#endif

        public Image Image { get; }

        /// <summary>Raised with a message when the recording cannot be played.</summary>
#pragma warning disable CS0067
        public event Action<string> Error;
#pragma warning restore CS0067
    }
}
