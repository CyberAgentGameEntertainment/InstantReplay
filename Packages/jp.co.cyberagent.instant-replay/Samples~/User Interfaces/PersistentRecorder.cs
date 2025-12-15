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
        private const float BitPerPixel = 6f;
        private const float BitPerPixelBias = -25000f;
        private const float LowerBitPerPixel = 3f;
        private const float LowerBitPerPixelBias = 1000f;

        #region Serialized Fields

        [FormerlySerializedAs("maxWidth")] [SerializeField] public int width = 640;
        [FormerlySerializedAs("maxHeight")] [SerializeField] public int height = 640;
        [FormerlySerializedAs("maxMemoryUsageMb")] [SerializeField] public int maxMemoryUsageForCompressedMb = 20;
        [SerializeField] public int maxNumberOfRawFrameBuffers = 4;
        [SerializeField] public int fixedFrameRate = 30;

        #endregion

        private RealtimeInstantReplaySession _currentSession;

        public bool IsPaused => _currentSession?.IsPaused ?? true;

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

            var box = new Box<RealtimeInstantReplaySession>();
            box.Value = _currentSession = new RealtimeInstantReplaySession(new RealtimeEncodingOptions
            {
                VideoOptions = new VideoEncoderOptions
                {
                    Width = (uint)width,
                    Height = (uint)height,

                    Bitrate = (uint)Mathf.Max(
                        // approximates the values YouTube recommends https://support.google.com/youtube/answer/1722171?hl=ja
                        width * height * BitPerPixel + BitPerPixelBias,
                        // and a lower bound
                        width * height * LowerBitPerPixel + LowerBitPerPixelBias
                    ),
                    FpsHint = (uint)fixedFrameRate
                },
                AudioOptions = new AudioEncoderOptions
                {
                    Channels = 2,
                    SampleRate = (uint)AudioSettings.outputSampleRate,
                    Bitrate = 128000
                },
                MaxNumberOfRawFrameBuffers = maxNumberOfRawFrameBuffers,
                MaxMemoryUsageBytesForCompressedFrames = maxMemoryUsageForCompressedMb * 1024 * 1024,
                FixedFrameRate = 30.0, // null if not using fixed frame rate
                VideoInputQueueSize = 5, // Maximum number of raw frames to keep before encoding
                AudioInputQueueSizeSeconds =
                    1.0 // Max queued audio input duration to be buffered before encoding, in seconds
            }, onException: ex =>
            {
                Debug.LogException(ex);
                if (box.Value == null || box.Value == _currentSession)
                {
                    NewSession();
                }
            });
        }

        public async ValueTask<string> StopAndTranscodeAsync(IProgress<float> progress, string directory)
        {
            try
            {
                using var session = _currentSession;
                _currentSession = null;

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

        public void Pause()
        {
            _currentSession?.Pause();
        }

        public void Resume()
        {
            _currentSession?.Resume();
        }

        private class Box<T>
        {
            public T Value;
        }
    }
}
