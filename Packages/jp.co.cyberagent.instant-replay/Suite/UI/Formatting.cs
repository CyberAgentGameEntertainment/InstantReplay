// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Globalization;

namespace InstantReplay.Suite.UI
{
    internal static class Formatting
    {
        public static string Time(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            var span = TimeSpan.FromSeconds(Math.Floor(seconds));
            return span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>Formats seconds with tenths, for positions within a recording.</summary>
        public static string PreciseTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            var tenths = (long)Math.Round(seconds * 10);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2}", tenths / 600, tenths / 10 % 60,
                tenths % 10);
        }

        public static string Bytes(long bytes)
        {
            const double kib = 1024, mib = kib * 1024, gib = mib * 1024;
            return bytes switch
            {
                >= (long)gib => string.Format(CultureInfo.InvariantCulture, "{0:0.0} GiB", bytes / gib),
                >= (long)mib => string.Format(CultureInfo.InvariantCulture, "{0:0.0} MiB", bytes / mib),
                >= (long)kib => string.Format(CultureInfo.InvariantCulture, "{0:0} KiB", bytes / kib),
                _ => string.Format(CultureInfo.InvariantCulture, "{0} B", bytes)
            };
        }

        public static string LocalDateTime(DateTime utc)
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}
