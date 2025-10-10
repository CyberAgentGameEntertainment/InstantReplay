// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

namespace InstantReplay
{
    internal interface IRecordingTimeProvider
    {
        double Now { get; }
        bool IsPaused { get; }
        double TotalPausedDuration { get; }
    }
}
