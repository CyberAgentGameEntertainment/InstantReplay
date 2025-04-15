// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace InstantReplay.Examples
{
    public class PersistentRecorder : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] public int maxWidth = 640;
        [SerializeField] public int maxHeight = 640;
        [SerializeField] public int numFrames = 900;
        [SerializeField] public int fixedFrameRate = 30;

        #endregion

        private InstantReplaySession _currentSession;

        #region Event Functions

        private void OnEnable()
        {
            NewSession(false);
        }

        private void OnDisable()
        {
            if (_currentSession == null)
                return;
            _currentSession.Dispose();
            _currentSession = null;
        }

        #endregion

        public void NewSession(bool allowStopCurrentSession = true)
        {
            if (!isActiveAndEnabled)
            {
                Debug.LogWarning("Recorder is not enabled");
                return;
            }

            if (_currentSession != null)
            {
                if (allowStopCurrentSession)
                {
                    _currentSession.Dispose();
                    _currentSession = null;
                }
                else
                {
                    return;
                }
            }

            _currentSession =
                new InstantReplaySession(numFrames, fixedFrameRate, maxWidth: maxWidth, maxHeight: maxHeight);
        }

        public async ValueTask<string> StopAndTranscodeAsync(IProgress<float> progress)
        {
            using var session = _currentSession;
            _currentSession = null;

            try
            {
                if (!isActiveAndEnabled || session == null)
                {
                    Debug.LogWarning("Recorder is not enabled");
                    return null;
                }

                var outputFilename = await session.StopAndTranscodeAsync(progress);

                if (string.IsNullOrEmpty(outputFilename))
                    return null;

                var dest = Path.Combine(Application.temporaryCachePath, Path.GetFileName(outputFilename));
                File.Move(outputFilename, dest);

                return dest;
            }
            finally
            {
                if (isActiveAndEnabled)
                    NewSession(false);
            }
        }
    }
}
