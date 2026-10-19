// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     Destination to which recordings can be shared, such as a directory, a chat service, or an issue tracker.
    /// </summary>
    /// <remarks>
    ///     Targets are assets so that they can be configured in the inspector and listed in
    ///     <see cref="InstantReplaySuiteSettings.ShareTargets" />. Create one from code with
    ///     <see cref="ScriptableObject.CreateInstance{T}" /> to supply settings at runtime. Members are called on the main
    ///     thread.
    /// </remarks>
    public abstract class ShareTarget : ScriptableObject
    {
        [Tooltip("Name shown to the user. The asset name is used when empty.")] [SerializeField]
        private string displayName;

        /// <summary>Name shown to the user.</summary>
        public virtual string DisplayName
        {
            get => string.IsNullOrEmpty(displayName) ? name : displayName;
            set => displayName = value;
        }

        /// <summary>
        ///     Whether the request can be shared, for example within the size limit of the destination.
        /// </summary>
        /// <param name="request">The recordings to share.</param>
        /// <param name="reason">Explanation shown to the user when false.</param>
        public virtual bool CanShare(ShareRequest request, out string reason)
        {
            reason = null;
            return true;
        }

        /// <summary>
        ///     Shares the recordings. Throws when sharing fails; the message of the exception is shown to the user.
        /// </summary>
        public abstract ValueTask<ShareResult> ShareAsync(ShareRequest request, ShareContext context,
            CancellationToken cancellationToken);
    }

    /// <summary>
    ///     What to share through a <see cref="ShareTarget" />.
    /// </summary>
    public sealed class ShareRequest
    {
        public ShareRequest(IReadOnlyList<RecordingEntry> entries)
        {
            Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        }

        /// <summary>Recordings to share. Their metadata can be included in the message.</summary>
        public IReadOnlyList<RecordingEntry> Entries { get; }

        /// <summary>Text entered by the user. May be null.</summary>
        public string Comment { get; set; }

        /// <summary>Sum of the file sizes of <see cref="Entries" /> in bytes.</summary>
        public long TotalSizeBytes
        {
            get
            {
                long total = 0;
                foreach (var entry in Entries) total += entry.SizeBytes;
                return total;
            }
        }
    }

    /// <summary>
    ///     Services that the caller of <see cref="ShareTarget.ShareAsync" /> provides to the target.
    /// </summary>
    public sealed class ShareContext
    {
        private readonly IProgress<float> _progress;

        /// <param name="progress">Receives the progress from 0 to 1. May be null.</param>
        public ShareContext(IProgress<float> progress = null)
        {
            _progress = progress;
        }

        /// <summary>Reports the progress from 0 to 1.</summary>
        public void ReportProgress(float value)
        {
            _progress?.Report(Mathf.Clamp01(value));
        }
    }

    /// <summary>
    ///     Outcome of a successful <see cref="ShareTarget.ShareAsync" />.
    /// </summary>
    public readonly struct ShareResult
    {
        /// <param name="message">Text shown to the user. May be null.</param>
        /// <param name="url">Location of the shared recordings, such as a message link or a directory. May be null.</param>
        public ShareResult(string message, string url = null)
        {
            Message = message;
            Url = url;
        }

        public string Message { get; }
        public string Url { get; }
    }
}
