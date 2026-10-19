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
    ///     module is disabled.
    /// </summary>
    internal sealed class PreviewPlayer : IDisposable
    {
#if INSTANTREPLAY_VIDEO && !UNITY_WEBGL
        private readonly GameObject _gameObject;
        private readonly VideoPlayer _player;
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
            _player.errorReceived += (_, message) => Error?.Invoke(message);
        }

        public static bool IsSupported => true;

        public bool IsPlaying => _player != null && _player.isPlaying;

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
