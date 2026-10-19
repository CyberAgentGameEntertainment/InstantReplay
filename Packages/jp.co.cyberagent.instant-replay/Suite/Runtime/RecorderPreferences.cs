// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using UnityEngine;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     Choices made by the user that survive a relaunch.
    /// </summary>
    [Serializable]
    internal sealed class RecorderPreferences
    {
        private const int CurrentVersion = 1;

        public int version = CurrentVersion;
        public string selectedPresetId;
        public bool hasBufferingEnabled;
        public bool bufferingEnabled;

        private static string FilePath =>
            Path.Combine(Application.persistentDataPath, "InstantReplay", "suite-preferences.json");

        public static RecorderPreferences Load()
        {
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return new RecorderPreferences();
                var loaded = JsonUtility.FromJson<RecorderPreferences>(File.ReadAllText(path));
                return loaded is { version: CurrentVersion } ? loaded : new RecorderPreferences();
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
                return new RecorderPreferences();
            }
        }

        public void Save()
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(this));
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }
        }
    }
}
