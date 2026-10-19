// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using UnityEngine;
using UnityEngine.UIElements;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Rows that list recordings, shared by the Recordings tab and the recording picker.
    /// </summary>
    internal static class RecordingRows
    {
        /// <param name="showCheckBox">Whether the row shows a check box. The row is highlighted either way when checked.</param>
        public static VisualElement Create(string title, string details, Texture2D thumbnail, bool showCheckBox,
            bool isChecked)
        {
            var row = new VisualElement();
            row.AddToClassList("irs-library__row");
            row.EnableInClassList("irs-library__row--checked", isChecked);

            if (showCheckBox)
            {
                // The row handles the click, so the check box only displays the state.
                var check = new Toggle { value = isChecked, pickingMode = PickingMode.Ignore };
                check.AddToClassList("irs-library__check");
                foreach (var child in check.Query<VisualElement>().ToList()) child.pickingMode = PickingMode.Ignore;
                row.Add(check);
            }

            if (thumbnail != null)
            {
                var image = new Image { image = thumbnail, scaleMode = ScaleMode.ScaleAndCrop };
                image.AddToClassList("irs-thumbnail");
                row.Add(image);
            }

            var text = new VisualElement();
            text.AddToClassList("irs-library__row-text");
            row.Add(text);

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("irs-library__row-title");
            text.Add(titleLabel);

            var detailsLabel = new Label(details);
            detailsLabel.AddToClassList("irs-panel__details");
            text.Add(detailsLabel);
            return row;
        }

        public static string Title(RecordingEntry entry)
        {
            return $"{entry.Source} · {Formatting.LocalDateTime(entry.CreatedAtUtc)}";
        }

        public static string DescribeShort(RecordingEntry entry)
        {
            return entry.DurationSeconds > 0
                ? $"{Formatting.Time(entry.DurationSeconds)} · {Formatting.Bytes(entry.SizeBytes)}"
                : Formatting.Bytes(entry.SizeBytes);
        }
    }
}
