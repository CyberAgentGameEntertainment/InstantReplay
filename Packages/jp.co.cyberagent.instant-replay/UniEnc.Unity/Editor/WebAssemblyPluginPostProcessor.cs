using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UniEnc.Unity.Editor
{
    public class WebAssemblyPluginPostProcessor : AssetPostprocessor
    {
        private const string EmscriptenVersion =
#if UNITY_2023_2_OR_NEWER
                "3.1.38-unity"
#elif UNITY_2022_2_OR_NEWER
                "3.1.8-unity"
#else
                null
#endif
            ;

        private static bool _isEditing = false;

        public override uint GetVersion()
        {
            return 0;
        }

        private void OnPreprocessAsset()
        {
            HandleAsset(assetPath);
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (importedAssets.Length == 0)
            {
                return;
            }

            try
            {
                foreach (var assetPath in importedAssets)
                {
                    HandleAsset(assetPath);
                }
            }
            finally
            {
                if (_isEditing)
                {
                    _isEditing = false;
                    AssetDatabase.StopAssetEditing();
                }
            }
        }

        private static void HandleAsset(string path)
        {
            if (!path.EndsWith("libunienc_c.a", StringComparison.OrdinalIgnoreCase)) return;
            var dir = Path.GetFileName(Path.GetDirectoryName(path));

#pragma warning disable CS0162
            if (EmscriptenVersion == null) Debug.LogError("Instant Replay requires Unity 2022.3.");
#pragma warning restore CS0162

            var compatible = dir?.EndsWith(EmscriptenVersion) ?? false;

            if (!_isEditing)
            {
                _isEditing = true;
                AssetDatabase.StartAssetEditing();
            }

            var importer = (PluginImporter)AssetImporter.GetAtPath(path);
            importer.SetCompatibleWithAnyPlatform(false);
            var current = importer.GetCompatibleWithPlatform(BuildTarget.WebGL);

            if (current == compatible) return;
            importer.SetCompatibleWithPlatform(BuildTarget.WebGL, compatible);
            importer.SaveAndReimport();
            AssetDatabase.Refresh();
        }
    }
}
