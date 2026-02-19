#if EXCLUDE_INSTANTREPLAY

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UniEnc.Unity.Editor
{
    internal class PluginsExcluder : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        private readonly List<string> _excluded = new();

        void IPreprocessBuildWithReport.OnPreprocessBuild(BuildReport report)
        {
            _excluded.Clear();
            foreach (var plugin in PluginImporter.GetAllImporters())
            {
                if (!plugin.assetPath.StartsWith("Packages/jp.co.cyberagent.instant-replay/UniEnc/Plugins/")) continue;
                if (!plugin.GetCompatibleWithPlatform(report.summary.platform)) continue;
                _excluded.Add(plugin.assetPath);
                plugin.SetCompatibleWithPlatform(report.summary.platform, false);
                plugin.SaveAndReimport();
            }
        }

        void IPostprocessBuildWithReport.OnPostprocessBuild(BuildReport report)
        {
            foreach (var path in _excluded)
            {
                var plugin = (PluginImporter)AssetImporter.GetAtPath(path);
                plugin.SetCompatibleWithPlatform(report.summary.platform, true);
                plugin.SaveAndReimport();
            }

            _excluded.Clear();
        }
    }
}
#endif
