using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace InstantReplay.Editor
{
    /// <summary>
    ///     Processes the Xcode project after the build to ensure the app includes libswift_Concurrency.dylib required by the
    ///     transcoder plugin on older iOS.
    /// </summary>
    internal static class OldIosWorkaroundPostProcessor
    {
#if !INSTANTREPLAY_DISABLE_OLD_IOS_WORKAROUND
        [PostProcessBuild]
        public static void OnPostProcessBuild(BuildTarget buildTarget, string path)
        {
            if (buildTarget != BuildTarget.iOS) return;

            var pbxPath = PBXProject.GetPBXProjectPath(path);

            var project = new PBXProject();
            project.ReadFromFile(pbxPath);

            project.SetBuildProperty(project.GetUnityMainTargetGuid(), "ALWAYS_EMBED_SWIFT_STANDARD_LIBRARIES", "YES");

            project.WriteToFile(pbxPath);
        }
#endif
    }
}
