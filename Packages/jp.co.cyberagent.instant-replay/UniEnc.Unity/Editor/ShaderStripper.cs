using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UniEnc.Unity.Editor
{
    public class ShaderStripper : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private const string StripShaderName = "Hidden/InstantReplay/Rechannel";

        private string _shaderPath;
        private string _shaderBackupPath;

        public int callbackOrder => 0;

        void IPreprocessBuildWithReport.OnPreprocessBuild(BuildReport report)
        {
            var shader = Shader.Find(StripShaderName);
            if (shader == null) return;
            _shaderPath = AssetDatabase.GetAssetPath(shader);
            _shaderBackupPath = $"{_shaderPath}.bk";
            var error = AssetDatabase.MoveAsset(_shaderPath, _shaderBackupPath);
            if (!string.IsNullOrEmpty(error))
            {
                throw new BuildFailedException(error);
            }

            AssetDatabase.Refresh();
        }

        void IPostprocessBuildWithReport.OnPostprocessBuild(BuildReport report)
        {
            if (string.IsNullOrEmpty(_shaderBackupPath)) return;
            var error = AssetDatabase.MoveAsset(_shaderBackupPath, _shaderPath);
            if (!string.IsNullOrEmpty(error))
            {
                throw new BuildFailedException(error);
            }

            AssetDatabase.Refresh();
        }
    }
}
