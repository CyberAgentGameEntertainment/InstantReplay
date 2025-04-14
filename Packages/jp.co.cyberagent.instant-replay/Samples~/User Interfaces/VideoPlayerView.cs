// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace InstantReplay.Examples
{
    public class VideoPlayerView : MonoBehaviour
    {
        private static readonly Vector3[] Corners = new Vector3[4];

        #region Serialized Fields

        [SerializeField] private VideoPlayer videoPlayer;
        [SerializeField] private Button pauseResumeButton;
        [SerializeField] private Slider progressSlider;
        [SerializeField] private Slider volumeSlider;
        [SerializeField] private Button closeButton;
        [SerializeField] private RawImage display;

        #endregion

        private RenderTexture _renderTexture;

        #region Event Functions

        private void Update()
        {
            UpdateRenderTexture();

            if (!videoPlayer.isPlaying)
                return;

            progressSlider.SetValueWithoutNotify((float)videoPlayer.frame / videoPlayer.frameCount);
        }

        private void OnEnable()
        {
            pauseResumeButton.onClick.AddListener(OnPauseResumeButtonClick);
            progressSlider.onValueChanged.AddListener(OnProgressSliderValueChanged);
            volumeSlider.onValueChanged.AddListener(OnVolumeSliderValueChanged);
            closeButton.onClick.AddListener(OnCloseButtonClick);
        }

        private void OnDisable()
        {
            pauseResumeButton.onClick.RemoveListener(OnPauseResumeButtonClick);
            progressSlider.onValueChanged.RemoveListener(OnProgressSliderValueChanged);
            volumeSlider.onValueChanged.RemoveListener(OnVolumeSliderValueChanged);
            closeButton.onClick.RemoveListener(OnCloseButtonClick);
        }

        private void OnDestroy()
        {
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
            }
        }

        #endregion

        public void Open(string path)
        {
            videoPlayer.url = path;
            videoPlayer.Play();
            gameObject.SetActive(true);
        }

        private void UpdateRenderTexture()
        {
            display.rectTransform.GetWorldCorners(Corners);
            var worldWidth = (int)Vector3.Distance(Corners[0], Corners[3]);
            var worldHeight = (int)Vector3.Distance(Corners[0], Corners[1]);

            if (_renderTexture == null || _renderTexture.width != worldWidth || _renderTexture.height != worldHeight)
            {
                if (_renderTexture != null)
                {
                    _renderTexture.Release();
                    Destroy(_renderTexture);
                }

                _renderTexture = new RenderTexture(worldWidth, worldHeight, 0);
                videoPlayer.targetTexture = _renderTexture;
                display.texture = _renderTexture;
            }
        }

        private void OnPauseResumeButtonClick()
        {
            if (videoPlayer.isPlaying)
                videoPlayer.Pause();
            else
                videoPlayer.Play();
        }

        private void OnProgressSliderValueChanged(float value)
        {
            if (videoPlayer.isPlaying)
                videoPlayer.Pause();

            var frame = (long)(value * videoPlayer.frameCount);
            videoPlayer.frame = frame;
        }

        private void OnVolumeSliderValueChanged(float value)
        {
            videoPlayer.SetDirectAudioVolume(0, value);
        }

        private void OnCloseButtonClick()
        {
            videoPlayer.Stop();
            gameObject.SetActive(false);
        }
    }
}
