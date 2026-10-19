// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace InstantReplay.Suite.Sharing.Discord
{
    /// <summary>
    ///     Posts recordings to a Discord channel through a webhook.
    /// </summary>
    /// <remarks>
    ///     Discord limits the total size of the attachments of a message, and <see cref="CanShare" /> rejects requests
    ///     that exceed <see cref="MaxUploadMegabytes" />. A webhook URL stored in this asset is included in the build, so
    ///     use the asset only in development and testing builds, or set <see cref="WebhookUrl" /> at runtime from a
    ///     source that is not part of the build.
    /// </remarks>
    [CreateAssetMenu(fileName = "DiscordShareTarget", menuName = "Instant Replay/Share Targets/Discord")]
    public sealed class DiscordShareTarget : ShareTarget
    {
        private const int MaxAttachments = 10;
        private const int MaxContentLength = 2000;

        [Tooltip("Webhook URL of the channel (https://discord.com/api/webhooks/...).")] [SerializeField]
        private string webhookUrl;

        [Tooltip("Upload limit of the server in MiB. Recordings larger in total are not offered.")] [SerializeField]
        private int maxUploadMegabytes = 10;

        [Tooltip("Append the metadata of the recordings to the message.")] [SerializeField]
        private bool includeMetadata = true;

        /// <summary>Webhook URL of the channel.</summary>
        public string WebhookUrl
        {
            get => webhookUrl;
            set => webhookUrl = value;
        }

        /// <summary>
        ///     Upload limit in MiB, which depends on the boost level of the server. Requests larger in total are rejected.
        /// </summary>
        public int MaxUploadMegabytes
        {
            get => maxUploadMegabytes;
            set => maxUploadMegabytes = value;
        }

        /// <summary>Whether the metadata of the recordings is appended to the message.</summary>
        public bool IncludeMetadata
        {
            get => includeMetadata;
            set => includeMetadata = value;
        }

        public override bool CanShare(ShareRequest request, out string reason)
        {
            if (string.IsNullOrEmpty(webhookUrl))
            {
                reason = "The Discord webhook URL is not configured.";
                return false;
            }

            if (request != null)
            {
                if (request.Entries.Count > MaxAttachments)
                {
                    reason = $"Discord accepts at most {MaxAttachments} files per message.";
                    return false;
                }

                var limit = (long)Math.Max(0, maxUploadMegabytes) * 1024 * 1024;
                if (request.TotalSizeBytes > limit)
                {
                    reason = string.Format(CultureInfo.InvariantCulture,
                        "{0:0.0} MiB exceeds the Discord upload limit of {1} MiB.",
                        request.TotalSizeBytes / (1024.0 * 1024.0), maxUploadMegabytes);
                    return false;
                }
            }

            reason = null;
            return true;
        }

        public override async ValueTask<ShareResult> ShareAsync(ShareRequest request, ShareContext context,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!CanShare(request, out var reason)) throw new InvalidOperationException(reason);

            var payload = new Payload { content = ComposeMessage(request) };
            var sections = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("payload_json", Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)),
                    "application/json")
            };
            for (var i = 0; i < request.Entries.Count; i++)
            {
                var path = request.Entries[i].FilePath;
                // The size limit keeps the files small enough to be read into memory.
                var bytes = await Task.Run(() => File.ReadAllBytes(path), cancellationToken);
                sections.Add(new MultipartFormFileSection($"files[{i}]", bytes, Path.GetFileName(path), "video/mp4"));
            }

            using var webRequest = UnityWebRequest.Post(webhookUrl, sections);
            await WebRequests.SendAsync(webRequest, p => context?.ReportProgress(p), cancellationToken);
            context?.ReportProgress(1f);
            return new ShareResult($"Posted {request.Entries.Count} file(s) to Discord");
        }

        private string ComposeMessage(ShareRequest request)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(request.Comment)) builder.AppendLine(request.Comment);
            if (includeMetadata)
                foreach (var entry in request.Entries)
                foreach (var pair in entry.Metadata)
                    builder.Append(pair.Key).Append(": ").AppendLine(pair.Value);
            var message = builder.ToString().TrimEnd();
            return message.Length <= MaxContentLength ? message : message.Substring(0, MaxContentLength - 1) + "…";
        }

#pragma warning disable IDE1006
        [Serializable]
        private sealed class Payload
        {
            public string content;
        }
#pragma warning restore IDE1006
    }
}
