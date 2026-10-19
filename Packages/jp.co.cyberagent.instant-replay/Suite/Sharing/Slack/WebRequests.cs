// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace InstantReplay.Suite.Sharing.Slack
{
    internal static class WebRequests
    {
        private const int PollIntervalMs = 100;

        /// <summary>
        ///     Sends the request and waits for it on the calling thread, which must be the main thread. Throws when the
        ///     request fails at the transport level or returns an HTTP error.
        /// </summary>
        public static async Task<string> SendAsync(UnityWebRequest request, Action<float> uploadProgress,
            CancellationToken cancellationToken)
        {
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    request.Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                uploadProgress?.Invoke(request.uploadProgress);
                // The continuation returns to the main thread through the Unity synchronization context.
                await Task.Delay(PollIntervalMs);
            }

            var body = request.downloadHandler?.text;
            if (request.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException(
                    $"{request.method} {new Uri(request.url).Host} failed: {request.error}" +
                    (string.IsNullOrEmpty(body) ? string.Empty : $" {Truncate(body, 300)}"));

            return body;
        }

        private static string Truncate(string value, int length)
        {
            return value.Length <= length ? value : value.Substring(0, length) + "…";
        }
    }
}
