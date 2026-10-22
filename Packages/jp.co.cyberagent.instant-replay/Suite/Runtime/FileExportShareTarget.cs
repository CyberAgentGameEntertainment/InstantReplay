// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     Copies recordings to a directory, for example one that is synchronized or collected from the device.
    /// </summary>
    [CreateAssetMenu(fileName = "FileExportShareTarget", menuName = "Instant Replay/Share Targets/File Export")]
    public sealed class FileExportShareTarget : ShareTarget
    {
        private const string DefaultDirectory = "InstantReplay/Exports";

        #region Serialized Fields

        [Tooltip("Relative paths are resolved against Application.persistentDataPath.")] [SerializeField]
        private string directory = DefaultDirectory;

        [Tooltip("Open the directory after copying. Effective on desktop platforms.")] [SerializeField]
        private bool openDirectory;

        #endregion

        /// <summary>
        ///     Destination directory. Relative paths are resolved against <see cref="Application.persistentDataPath" />.
        /// </summary>
        public string Directory
        {
            get => directory;
            set => directory = value;
        }

        /// <summary>Whether the directory is opened after copying. Effective on desktop platforms.</summary>
        public bool OpenDirectory
        {
            get => openDirectory;
            set => openDirectory = value;
        }

        /// <summary>Returns the absolute path of <see cref="Directory" />.</summary>
        public string ResolveDirectory()
        {
            var path = string.IsNullOrEmpty(directory) ? DefaultDirectory : directory;
            return Path.IsPathRooted(path) ? path : Path.Combine(Application.persistentDataPath, path);
        }

        public override async ValueTask<ShareResult> ShareAsync(ShareRequest request, ShareContext context,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var destination = ResolveDirectory();
            var sources = new string[request.Entries.Count];
            for (var i = 0; i < sources.Length; i++) sources[i] = request.Entries[i].FilePath;

            var progress = context != null ? new Progress<float>(context.ReportProgress) : null;
            await Task.Run(() => Copy(sources, destination, progress, cancellationToken), cancellationToken);

            if (openDirectory) Application.OpenURL(new Uri(destination).AbsoluteUri);

            var url = new Uri(destination).AbsoluteUri;
            return new ShareResult($"Copied {sources.Length} file(s) to {destination}", url);
        }

        private static void Copy(string[] sources, string destination, IProgress<float> progress,
            CancellationToken cancellationToken)
        {
            System.IO.Directory.CreateDirectory(destination);
            for (var i = 0; i < sources.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[i];
                var name = Path.GetFileNameWithoutExtension(source);
                var extension = Path.GetExtension(source);
                var target = Path.Combine(destination, name + extension);
                for (var n = 1; File.Exists(target); n++)
                    target = Path.Combine(destination, $"{name}_{n}{extension}");
                File.Copy(source, target);
                progress?.Report((i + 1f) / sources.Length);
            }
        }
    }
}
