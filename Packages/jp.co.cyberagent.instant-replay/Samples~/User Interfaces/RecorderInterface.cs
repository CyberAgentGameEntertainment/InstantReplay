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
        [SerializeField] private OutputLocationKind outputLocation = OutputLocationKind.ApplicationTemporaryCachePath;
        [SerializeField] private string outputDirectory = "InstantReplayVideos";

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
            _ = Wrap();
            return;

            async ValueTask Wrap()
            {
                try
                {
                    await StopAndTranscodeAsync();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        public async ValueTask<string> StopAndTranscodeAsync()
        {
            try
            {
                ShowText("Transcoding...");
                transcodingProgressImage.fillAmount = 0f;
                var location = outputLocation switch
                {
                    OutputLocationKind.ApplicationPersistentDataPath => Application.persistentDataPath,
                    OutputLocationKind.ApplicationTemporaryCachePath => Application.temporaryCachePath,
                    _ => throw new ArgumentOutOfRangeException()
                };
                var directory = Path.Combine(location, outputDirectory);
                var outputFileName = await recorder.StopAndTranscodeAsync(
                    new Progress<float>(value => { transcodingProgressImage.fillAmount = value; }),
                    directory
                );

                if (string.IsNullOrEmpty(outputFileName))
                {
                    ShowText("No data to save", 3f);
                    return null;
                }

                ShowText($"Video saved: {outputFileName}", 10f);

                videoPlayerView.Open(outputFileName);
                return outputFileName;
            }
            catch (Exception)
            {
                ShowText("Failed to save video", 3f);
                throw;
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

        #region Nested type

        private enum OutputLocationKind
        {
            ApplicationPersistentDataPath,
            ApplicationTemporaryCachePath
        }

        #endregion
    }
}
