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
    public class Recorder : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private int maxWidth = 640;
        [SerializeField] private int maxHeight = 640;
        [SerializeField] private int numFrames = 900;
        [SerializeField] private int fixedFrameRate = 30;
        [SerializeField] private GameObject transcodingPanel;
        [SerializeField] private Text transcodingProgressText;
        [SerializeField] private Image transcodingProgressImage;

        #endregion

        private InstantReplaySession _currentSession;
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

        private void OnEnable()
        {
            NewSession();
            ShowText("Recording...", 3f);
        }


        private void OnDisable()
        {
            _currentSession.Dispose();
            _currentSession = null;
        }

        #endregion

        private void NewSession()
        {
            if (_currentSession != null) return;
            _currentSession = new InstantReplaySession(numFrames, fixedFrameRate, maxWidth: 640, maxHeight: 640);
        }

        public void StopAndTranscode()
        {
            _ = StopAndTranscodeAsync();
        }

        private async ValueTask StopAndTranscodeAsync()
        {
            try
            {
                if (!enabled || _currentSession == null)
                {
                    Debug.LogWarning("Recorder is not enabled");
                    return;
                }

                var session = _currentSession;
                _currentSession = null;
                ShowText("Transcoding...");
                transcodingProgressImage.fillAmount = 0f;
                var outputFileName = await session.StopAndTranscodeAsync(new Progress<float>(value =>
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
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                ShowText("Failed to save video", 3f);
            }
            finally
            {
                if (enabled)
                    NewSession();

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
