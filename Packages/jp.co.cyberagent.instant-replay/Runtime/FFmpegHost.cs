// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace InstantReplay
{
    internal class FFmpegHost : IDisposable
    {
        private readonly Process process;

        public FFmpegHost(string arguments, bool redirectStandardInput)
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
                    FileName = "/bin/bash",
                    Arguments = $"-cl 'ffmpeg {arguments}'",
#else
                    FileName = "ffmpeg",
                    Arguments = arguments,
#endif
                    RedirectStandardInput = redirectStandardInput,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
        }

        public StreamWriter StandardInput => process.StandardInput;

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            DisposeCore();
        }

        public async ValueTask RunAsync()
        {
            var tcs = new TaskCompletionSource<int>();
            process.Exited += (sender, e) => { tcs.SetResult(process.ExitCode); };
            process.Start();

            var results = await Task.WhenAll(process.StandardOutput.ReadToEndAsync(),
                process.StandardError.ReadToEndAsync());

            var code = await tcs.Task;

            if (code != 0)
                throw new Exception($"FFmpeg process exited ({code}). stdout:\n{results[0]}\n\nstderr:\n{results[1]}");

            if (string.IsNullOrEmpty(results[1]))
                Debug.LogWarning($"FFmpeg process exited ({code}). stdout:\n{results[0]}\n\nstderr:\n{results[1]}");
        }

        ~FFmpegHost()
        {
            DisposeCore();
        }

        private void DisposeCore()
        {
            process?.Dispose();
        }
    }
}
