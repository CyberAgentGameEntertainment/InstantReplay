// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System.Diagnostics;

namespace InstantReplay
{
    internal class TemporalController : IRecordingTimeProvider
    {
        private readonly object _lock = new();
        double IRecordingTimeProvider.Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        public bool IsPaused { get; private set; } = true;
        private double _pauseStartTime;
        private double _totalPausedDuration;

        double IRecordingTimeProvider.TotalPausedDuration => _totalPausedDuration;

        /// <summary>
        ///     Starts recording.
        /// </summary>
        public void Resume()
        {
            lock (_lock)
            {
                if (!IsPaused)
                    return;

                _totalPausedDuration += ((IRecordingTimeProvider)this).Now - _pauseStartTime;

                IsPaused = false;
            }
        }

        /// <summary>
        ///     Stops recording.
        /// </summary>
        public void Pause()
        {
            lock (_lock)
            {
                if (IsPaused)
                    return;

                _pauseStartTime = ((IRecordingTimeProvider)this).Now;

                IsPaused = true;
            }
        }
    }
}
