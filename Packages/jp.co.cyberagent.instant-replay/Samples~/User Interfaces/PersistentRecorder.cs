// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using System.Threading.Tasks;
using UniEnc;
using UnityEngine;
using UnityEngine.Serialization;

namespace InstantReplay.Examples
{
    public class PersistentRecorder : MonoBehaviour
    {
        #region Serialized Fields

        [FormerlySerializedAs("maxWidth")] [SerializeField] public int width = 640;
        [FormerlySerializedAs("maxHeight")] [SerializeField] public int height = 640;
        [SerializeField] public int maxMemoryUsageMb = 20;
        [SerializeField] public int fixedFrameRate = 30;

        #endregion

        private RealtimeInstantReplaySession _currentSession;

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

            _currentSession = new RealtimeInstantReplaySession(new RealtimeEncodingOptions()
            {
                VideoOptions = new VideoEncoderOptions()
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    Bitrate = (uint)Mathf.Min(width * height * 30 * 0.2f - 25000,
                        width * height * 30 * 0.1f + 1000),
                    FpsHint = (uint)fixedFrameRate

                },
                AudioOptions = new AudioEncoderOptions
                {
                    Channels = 2,
                    SampleRate = (uint)AudioSettings.outputSampleRate,
                    Bitrate = 128000
                },
                MaxMemoryUsageBytes = maxMemoryUsageMb * 1024 * 1024, // 20 MiB
                FixedFrameRate = 30.0, // null if not using fixed frame rate
                VideoInputQueueSize = 5, // Maximum number of raw frames to keep before encoding
                AudioInputQueueSize = 60, // Maximum number of raw audio sample frames to keep before encoding
            });
        }

        public async ValueTask<string> StopAndTranscodeAsync(IProgress<float> progress, string directory)
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

                var outputFilename = await session.StopAndExportAsync();

                if (string.IsNullOrEmpty(outputFilename))
                    return null;

                var dest = Path.Combine(directory, Path.GetFileName(outputFilename));

                Directory.CreateDirectory(directory);

                // Some platforms do not support moving files between specific directories (e.g. Application.persistentDataPath and Application.temporaryCachePath)
                File.Copy(outputFilename, dest);

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
