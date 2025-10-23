// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace InstantReplay
{
    internal class FFmpegHost : IDisposable
    {
        private readonly Process _process;

        public FFmpegHost(string arguments, bool redirectStandardInput)
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
#if (!UNITY_EDITOR && UNITY_STANDALONE_OSX) || UNITY_EDITOR_OSX
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

        public StreamWriter StandardInput => _process.StandardInput;

        public void Dispose()
        {
            DisposeCore();
        }

        public async ValueTask RunAsync()
        {
            var tcs = new TaskCompletionSource<int>();
            _process.Exited += (_, _) => tcs.SetResult(_process.ExitCode);
            _process.Start();

            var results = await Task.WhenAll(_process.StandardOutput.ReadToEndAsync(),
                _process.StandardError.ReadToEndAsync());

            var code = await tcs.Task;

            if (code != 0)
                throw new Exception($"FFmpeg process exited ({code}). stdout:\n{results[0]}\n\nstderr:\n{results[1]}");

            if (string.IsNullOrEmpty(results[1]))
                ILogger.LogWarningCore(
                    $"FFmpeg process exited ({code}). stdout:\n{results[0]}\n\nstderr:\n{results[1]}");
        }

        ~FFmpegHost()
        {
            GC.SuppressFinalize(this);
            DisposeCore();
        }

        private void DisposeCore()
        {
            _process?.Dispose();
        }
    }
}
