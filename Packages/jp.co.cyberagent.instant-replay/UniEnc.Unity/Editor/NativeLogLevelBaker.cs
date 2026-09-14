// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

#if !EXCLUDE_INSTANTREPLAY

using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UniEnc.Unity.Editor
{
    /// <summary>
    ///     Writes the configured native log level into the build as a Resources asset, and takes it back
    ///     out afterwards.
    /// </summary>
    internal class NativeLogLevelBaker : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        /// <summary>
        ///     Generated under Assets because a package folder is read-only for anyone consuming this
        ///     package from a registry, and under Resources because that is the only load path that is
        ///     synchronous on every platform — StreamingAssets is not, on Android.
        /// </summary>
        private const string GeneratedFolder = "Assets/UniEnc.Generated";

        private const string ResourcesFolder = GeneratedFolder + "/Resources/UniEnc";

        private const string AssetPath = GeneratedFolder + "/Resources/" + NativeLogLevelAsset.ResourcePath + ".asset";

        public int callbackOrder => 0;

        void IPostprocessBuildWithReport.OnPostprocessBuild(BuildReport report)
        {
            Remove();
        }

        void IPreprocessBuildWithReport.OnPreprocessBuild(BuildReport report)
        {
            // A build that died before its postprocess ran leaves the folder behind, and a stale level is
            // worse than none.
            Remove();

            var level = NativeLoggingPreferences.StoredLevel;
            if (level == NativeLoggingPreferences.Unset || !NativeLoggingPreferences.BakeIntoBuilds) return;

            var asset = ScriptableObject.CreateInstance<NativeLogLevelAsset>();
            asset.Level = (NativeLogLevel)level;

            EnsureFolder(ResourcesFolder);
            AssetDatabase.CreateAsset(asset, AssetPath);
            AssetDatabase.SaveAssets();
        }

        [InitializeOnLoadMethod]
        private static void RemoveLeftovers()
        {
            // The asset database is not ready to delete anything this early in a domain reload, so wait
            // for a tick. `update` rather than `delayCall`, which is a plain delegate field that anyone
            // assigning to it instead of subscribing will silently drop this from.
            EditorApplication.update += RemoveOnce;
        }

        private static void RemoveOnce()
        {
            EditorApplication.update -= RemoveOnce;
            Remove();
        }

        private static void Remove()
        {
            if (!AssetDatabase.IsValidFolder(GeneratedFolder)) return;

            AssetDatabase.DeleteAsset(GeneratedFolder);
        }

        /// <summary>
        ///     Creates <paramref name="folder" /> and any missing parent, through the asset database so
        ///     that <see cref="AssetDatabase.CreateAsset" /> accepts the result.
        /// </summary>
        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;

            // Asset database paths are always '/'-separated, so this needs none of Path's separator
            // handling — and CreateFolder rejects a backslash.
            var split = folder.LastIndexOf('/');
            EnsureFolder(folder.Substring(0, split));
            AssetDatabase.CreateFolder(folder.Substring(0, split), folder.Substring(split + 1));
        }
    }
}

#endif
