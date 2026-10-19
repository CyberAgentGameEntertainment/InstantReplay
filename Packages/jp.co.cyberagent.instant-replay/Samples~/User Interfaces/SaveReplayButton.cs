// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using InstantReplay.Suite;
using InstantReplay.Suite.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace InstantReplay.Examples
{
    /// <summary>
    ///     Saves a replay from game code, in addition to the button in the overlay panel.
    /// </summary>
    public class SaveReplayButton : MonoBehaviour
    {
        [SerializeField] private float durationSeconds = 10f;

        public async void SaveReplay()
        {
            var suite = InstantReplaySuite.Current;
            if (suite == null || suite.Recorder.State != RecorderState.Buffering) return;

            try
            {
                var entry = await suite.Recorder.SaveReplayAsync(new ReplaySaveRequest
                {
                    DurationSeconds = durationSeconds,
                    Metadata = new Dictionary<string, string> { ["scene"] = SceneManager.GetActiveScene().name }
                });
                Debug.Log($"Replay saved: {entry.FilePath}");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}
