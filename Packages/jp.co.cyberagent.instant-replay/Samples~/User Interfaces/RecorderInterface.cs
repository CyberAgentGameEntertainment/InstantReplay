// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace InstantReplay.Examples
{
    public class RecorderInterface : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private GameObject transcodingPanel;
        [SerializeField] private Text transcodingProgressText;
        [SerializeField] private Image transcodingProgressImage;
        [SerializeField] private PersistentRecorder recorder;
        [SerializeField] private VideoPlayerView videoPlayerView;

        #endregion

        private float? _textExpires;

        #region Event Functions

        private void Update()
        {
            if (_textExpires.HasValue && Time.time > _textExpires.Value)
            {
                transcodingPanel.SetActive(false);
                _textExpires = null;
            }
        }

        #endregion

        public void StopAndTranscode()
        {
            _ = StopAndTranscodeAsync();
        }

        private async ValueTask StopAndTranscodeAsync()
        {
            try
            {
                ShowText("Transcoding...");
                transcodingProgressImage.fillAmount = 0f;
                var outputFileName = await recorder.StopAndTranscodeAsync(new Progress<float>(value =>
                {
                    transcodingProgressImage.fillAmount = value;
                }));

                if (string.IsNullOrEmpty(outputFileName))
                {
                    ShowText("No data to save", 3f);
                    return;
                }

                var dest = Path.Combine(Application.persistentDataPath, Path.GetFileName(outputFileName));
                File.Move(outputFileName, dest);

                ShowText($"Video saved: {dest}", 10f);

                videoPlayerView.Open(dest);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                ShowText("Failed to save video", 3f);
            }
            finally
            {
                transcodingProgressImage.fillAmount = 0f;
            }
        }

        private void ShowText(string text, float? duration = null)
        {
            transcodingProgressText.text = text;
            transcodingPanel.SetActive(true);

            if (duration.HasValue)
                _textExpires = Time.time + duration.Value;
        }
    }
}
