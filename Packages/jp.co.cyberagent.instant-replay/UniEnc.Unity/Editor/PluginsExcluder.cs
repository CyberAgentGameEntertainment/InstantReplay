using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace UniEnc.Unity.Editor
{
    public class PluginsExcluder : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        private readonly List<string> _excluded = new();

        void IPreprocessBuildWithReport.OnPreprocessBuild(BuildReport report)
        {
            _excluded.Clear();
            var plugins = PluginImporter.GetAllImporters()
                .Where(p => Regex.IsMatch(p.assetPath, @"/UniEnc/Plugins/.+\.(dll|dylib|a|so|mm)$"));
            foreach (var plugin in plugins)
            {
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