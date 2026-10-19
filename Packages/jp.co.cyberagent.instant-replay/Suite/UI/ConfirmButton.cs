// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine.UIElements;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Button for an action that cannot be undone. The first click asks for confirmation, and a second click within a
    ///     few seconds performs the action.
    /// </summary>
    internal sealed class ConfirmButton : Button
    {
        private const long ConfirmWindowMs = 3000;

        private readonly Action _action;
        private string _label;
        private IVisualElementScheduledItem _reset;
        private bool _armed;

        public ConfirmButton(string label, Action action)
        {
            _label = label;
            _action = action;
            text = label;
            clicked += OnClicked;
        }

        /// <summary>Text shown while the button is not asking for confirmation.</summary>
        public string Label
        {
            get => _label;
            set
            {
                _label = value;
                if (!_armed) text = value;
            }
        }

        public void Disarm()
        {
            _armed = false;
            _reset?.Pause();
            text = _label;
            RemoveFromClassList("irs-button--armed");
        }

        private void OnClicked()
        {
            if (!_armed)
            {
                _armed = true;
                text = "Tap again to confirm";
                AddToClassList("irs-button--armed");
                _reset?.Pause();
                _reset = schedule.Execute(Disarm).StartingIn(ConfirmWindowMs);
                return;
            }

            Disarm();
            _action();
        }
    }
}
