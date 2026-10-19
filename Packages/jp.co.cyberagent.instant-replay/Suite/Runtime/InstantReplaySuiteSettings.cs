// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     Project-wide configuration of the recorder: the presets the user can choose from and the initial behavior.
    /// </summary>
    [CreateAssetMenu(fileName = "InstantReplaySuiteSettings", menuName = "Instant Replay/Suite Settings")]
    public sealed class InstantReplaySuiteSettings : ScriptableObject
    {
        [SerializeField] private List<RecordingPreset> presets = new() { RecordingPreset.CreateDefault() };
        [SerializeField] private string defaultPresetId = "default";
        [SerializeField] private bool startBufferingOnLaunch = true;

        [Tooltip("Relative paths are resolved against Application.persistentDataPath.")] [SerializeField]
        private string outputDirectory = "InstantReplay/Recordings";

        [Tooltip("Remember the preset and the buffering toggle chosen by the user across launches.")] [SerializeField]
        private bool persistUserPreferences = true;

        public List<RecordingPreset> Presets => presets;

        public string DefaultPresetId
        {
            get => defaultPresetId;
            set => defaultPresetId = value;
        }

        /// <summary>
        ///     Whether buffering starts when the recorder is activated and the user has not chosen otherwise.
        /// </summary>
        public bool StartBufferingOnLaunch
        {
            get => startBufferingOnLaunch;
            set => startBufferingOnLaunch = value;
        }

        /// <summary>
        ///     Directory where recordings are saved. Relative paths are resolved against
        ///     <see cref="Application.persistentDataPath" />.
        /// </summary>
        public string OutputDirectory
        {
            get => outputDirectory;
            set => outputDirectory = value;
        }

        public bool PersistUserPreferences
        {
            get => persistUserPreferences;
            set => persistUserPreferences = value;
        }

        /// <summary>
        ///     Returns the preset with the given identifier, or null.
        /// </summary>
        public RecordingPreset FindPreset(string id)
        {
            if (presets == null || string.IsNullOrEmpty(id)) return null;
            foreach (var preset in presets)
                if (preset != null && preset.Id == id)
                    return preset;
            return null;
        }

        /// <summary>
        ///     Returns the default preset, falling back to the first preset, and to a built-in preset when none is defined.
        /// </summary>
        public RecordingPreset GetDefaultPreset()
        {
            var preset = FindPreset(defaultPresetId);
            if (preset != null) return preset;
            if (presets != null)
                foreach (var p in presets)
                    if (p != null)
                        return p;
            return RecordingPreset.CreateDefault();
        }

        /// <summary>
        ///     Returns the absolute path of <see cref="OutputDirectory" />.
        /// </summary>
        public string ResolveOutputDirectory()
        {
            var directory = string.IsNullOrEmpty(outputDirectory) ? "InstantReplay/Recordings" : outputDirectory;
            return Path.IsPathRooted(directory)
                ? directory
                : Path.Combine(Application.persistentDataPath, directory);
        }
    }
}
