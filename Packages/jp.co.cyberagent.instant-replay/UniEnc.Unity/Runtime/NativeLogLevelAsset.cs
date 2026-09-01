// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using UnityEngine;

namespace UniEnc.Unity
{
    /// <summary>
    ///     Carries the level configured in the Editor into a built player. A player connection can only
    ///     deliver a level once the player is already running, which is too late for the records the
    ///     encoder emits while the first session is starting up.
    /// </summary>
    internal class NativeLogLevelAsset : ScriptableObject
    {
        /// <summary>
        ///     Path handed to <see cref="Resources.Load{T}(string)" />, shared with the build hook that
        ///     writes the asset.
        /// </summary>
        internal const string ResourcePath = "UniEnc/NativeLogLevel";

        [SerializeField] private NativeLogLevel level = NativeLogLevel.Info;

        internal NativeLogLevel Level
        {
            get => level;
            set => level = value;
        }
    }
}
