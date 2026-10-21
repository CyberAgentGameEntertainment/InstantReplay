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

        // A reported position within this distance of the requested position is taken to have reached it.
        private const double SeekTolerance = 0.1;

        private readonly GameObject _gameObject;
        private readonly VideoPlayer _player;
        private float _lastSeekRealtime;
        private bool _seekCompleted;
        private bool _seeking;
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
                var seeking = IsSeeking;
                if (UnityEngine.Time.realtimeSinceStartup - _lastSeekRealtime < 2) LogSeek($"read (seeking={seeking})");
                return seeking ? _seekTarget : _player.time;
            }
        }

        // On Android, the player may report the completion of a seek before it reports the new position, so a seek lasts
        // until both the completion and the new position have been reported.
        private bool IsSeeking
        {
            get
            {
                if (!_seeking) return false;
                var elapsed = UnityEngine.Time.realtimeSinceStartup - _lastSeekRealtime;
                var time = _player.time;
                // While playing, the position may already have advanced past the requested position.
                var reached = time >= _seekTarget - SeekTolerance &&
                              time <= _seekTarget + SeekTolerance + (_player.isPlaying ? elapsed : 0);
                if (elapsed >= SeekTimeoutSeconds || (_seekCompleted && reached))
                {
                    _seeking = false;
                    LogSeek($"seek finished (timeout={elapsed >= SeekTimeoutSeconds})");
                }

                return _seeking;
            }
        }

        public void Seek(double seconds)
        {
            if (_player == null || !_player.isPrepared || !_player.canSetTime) return;
            _seekTarget = Math.Max(0, seconds);
            _seeking = true;
            _seekCompleted = false;
            _lastSeekRealtime = UnityEngine.Time.realtimeSinceStartup;
            LogSeek("seek requested");
            _player.time = _seekTarget;
        }

        // TODO: Remove once the seek behavior on Android has been confirmed.
        private void LogSeek(string message)
        {
            Debug.Log(
                $"[PreviewPlayer] {message}: target={_seekTarget:F3} time={_player.time:F3} frame={_player.frame} " +
                $"clock={_player.clockTime:F3} playing={_player.isPlaying} " +
                $"elapsed={UnityEngine.Time.realtimeSinceStartup - _lastSeekRealtime:F3}");
        }

        public void Pause()
        {
            if (_player != null && _player.isPlaying)
            {
                LogSeek("pause");
                _player.Pause();
            }
        }

        public void Resume()
        {
            if (_player != null && _player.isPrepared && !_player.isPlaying)
            {
                LogSeek("resume");
                _player.Play();
            }
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
            _seeking = false;
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

        // A completion may belong to an earlier seek when several are issued in a row, which the check of the reported
        // position in IsSeeking covers.
        private void OnSeekCompleted(VideoPlayer player)
        {
            _seekCompleted = true;
            LogSeek("seek completed");
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
