// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace InstantReplay.Suite.Sharing.Slack
{
    /// <summary>
    ///     Uploads recordings to a Slack channel with a bot token.
    /// </summary>
    /// <remarks>
    ///     The token needs the <c>files:write</c> scope, and the bot must be a member of the channel. A token stored in
    ///     this asset is included in the build, so use the asset only in development and testing builds, or set
    ///     <see cref="Token" /> at runtime from a source that is not part of the build.
    /// </remarks>
    [CreateAssetMenu(fileName = "SlackShareTarget", menuName = "Instant Replay/Share Targets/Slack")]
    public sealed class SlackShareTarget : ShareTarget
    {
        private const string ApiBase = "https://slack.com/api/";

        [Tooltip("Bot token (xoxb-...) with the files:write scope.")] [SerializeField]
        private string token;

        [Tooltip("ID of the channel to post to, such as C0123456789.")] [SerializeField]
        private string channelId;

        [Tooltip("Append the metadata of the recordings to the message.")] [SerializeField]
        private bool includeMetadata = true;

        /// <summary>Bot token with the <c>files:write</c> scope.</summary>
        public string Token
        {
            get => token;
            set => token = value;
        }

        /// <summary>ID of the channel to post to.</summary>
        public string ChannelId
        {
            get => channelId;
            set => channelId = value;
        }

        /// <summary>Whether the metadata of the recordings is appended to the message.</summary>
        public bool IncludeMetadata
        {
            get => includeMetadata;
            set => includeMetadata = value;
        }

        public override bool CanShare(ShareRequest request, out string reason)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(channelId))
            {
                reason = "The Slack token or channel is not configured.";
                return false;
            }

            reason = null;
            return true;
        }

        public override async ValueTask<ShareResult> ShareAsync(ShareRequest request, ShareContext context,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!CanShare(request, out var reason)) throw new InvalidOperationException(reason);

            var totalBytes = Math.Max(1, request.TotalSizeBytes);
            long uploadedBytes = 0;
            var files = new List<CompleteFile>();

            foreach (var entry in request.Entries)
            {
                var fileName = Path.GetFileName(entry.FilePath);
                var length = new FileInfo(entry.FilePath).Length;

                var ticket = await GetUploadUrlAsync(fileName, length, cancellationToken);

                using (var upload = new UnityWebRequest(ticket.upload_url, UnityWebRequest.kHttpVerbPOST))
                {
                    upload.uploadHandler = new UploadHandlerFile(entry.FilePath);
                    upload.downloadHandler = new DownloadHandlerBuffer();
                    var offset = uploadedBytes;
                    await WebRequests.SendAsync(upload,
                        p => context?.ReportProgress((float)((offset + p * length) / totalBytes)),
                        cancellationToken);
                }

                uploadedBytes += length;
                context?.ReportProgress((float)uploadedBytes / totalBytes);
                files.Add(new CompleteFile { id = ticket.file_id, title = fileName });
            }

            var completion = await CompleteUploadAsync(files, ComposeMessage(request), cancellationToken);
            var permalink = completion.files is { Length: > 0 } ? completion.files[0].permalink : null;
            return new ShareResult($"Uploaded {files.Count} file(s) to Slack", permalink);
        }

        private async Task<UploadTicket> GetUploadUrlAsync(string fileName, long length,
            CancellationToken cancellationToken)
        {
            var form = new WWWForm();
            form.AddField("filename", fileName);
            form.AddField("length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var request = UnityWebRequest.Post(ApiBase + "files.getUploadURLExternal", form);
            request.SetRequestHeader("Authorization", "Bearer " + token);
            var body = await WebRequests.SendAsync(request, null, cancellationToken);
            var response = JsonUtility.FromJson<UploadTicket>(body);
            ThrowIfFailed(response, "files.getUploadURLExternal");
            return response;
        }

        private async Task<CompleteResponse> CompleteUploadAsync(List<CompleteFile> files, string message,
            CancellationToken cancellationToken)
        {
            var payload = new CompletePayload
            {
                files = files.ToArray(),
                channel_id = channelId,
                initial_comment = message
            };
            var json = JsonUtility.ToJson(payload);

            using var request = new UnityWebRequest(ApiBase + "files.completeUploadExternal",
                UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json))
                { contentType = "application/json; charset=utf-8" };
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", "Bearer " + token);
            var body = await WebRequests.SendAsync(request, null, cancellationToken);
            var response = JsonUtility.FromJson<CompleteResponse>(body);
            ThrowIfFailed(response, "files.completeUploadExternal");
            return response;
        }

        private string ComposeMessage(ShareRequest request)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(request.Comment)) builder.AppendLine(request.Comment);
            if (includeMetadata)
                foreach (var entry in request.Entries)
                foreach (var pair in entry.Metadata)
                    builder.Append(pair.Key).Append(": ").AppendLine(pair.Value);
            return builder.ToString().TrimEnd();
        }

        private static void ThrowIfFailed(ApiResponse response, string method)
        {
            if (response == null) throw new InvalidOperationException($"Slack {method} returned no response.");
            if (!response.ok) throw new InvalidOperationException($"Slack {method} failed: {response.error}");
        }

        // Field names follow the Slack Web API.
#pragma warning disable IDE1006
        [Serializable]
        private class ApiResponse
        {
            public bool ok;
            public string error;
        }

        [Serializable]
        private sealed class UploadTicket : ApiResponse
        {
            public string upload_url;
            public string file_id;
        }

        [Serializable]
        private sealed class CompleteFile
        {
            public string id;
            public string title;
        }

        [Serializable]
        private sealed class CompletePayload
        {
            public CompleteFile[] files;
            public string channel_id;
            public string initial_comment;
        }

        [Serializable]
        private sealed class CompleteResponse : ApiResponse
        {
            public UploadedFile[] files;
        }

        [Serializable]
        private sealed class UploadedFile
        {
            public string id;
            public string permalink;
        }
#pragma warning restore IDE1006
    }
}
