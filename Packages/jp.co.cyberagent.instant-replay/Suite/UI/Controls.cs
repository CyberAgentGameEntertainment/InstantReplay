// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using UnityEngine.UIElements;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Variants of the standard controls that the style sheet renders differently.
    /// </summary>
    internal static class Controls
    {
        public const string Primary = "irs-button--primary";
        public const string Danger = "irs-button--danger";
        public const string Ghost = "irs-button--ghost";

        public static T WithClass<T>(this T element, string className) where T : VisualElement
        {
            element.AddToClassList(className);
            return element;
        }

        /// <summary>
        ///     Renders the toggle as a switch. The style sheet cannot add a knob to the check mark, so it is added here.
        /// </summary>
        public static Toggle AsSwitch(this Toggle toggle)
        {
            toggle.AddToClassList("irs-switch");
            var checkmark = toggle.Q(className: "unity-toggle__checkmark");
            if (checkmark != null)
            {
                var knob = new VisualElement { pickingMode = PickingMode.Ignore };
                knob.AddToClassList("irs-switch__knob");
                checkmark.Add(knob);
            }

            return toggle;
        }
    }
}
