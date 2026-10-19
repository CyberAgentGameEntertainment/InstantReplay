// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Timeline of a trim view: the selected range with a handle at each end, the playback position with a knob, and a
    ///     tick at every key frame. Dragging a handle or the knob raises the corresponding event with the time under the
    ///     pointer; the owner validates the time and sets it back.
    /// </summary>
    internal sealed class TrimTimeline : VisualElement
    {
        private const float HandleGrabRadius = 14;

        private readonly VisualElement _endHandle;
        private readonly VisualElement _playhead;
        private readonly VisualElement _range;
        private readonly VisualElement _startHandle;
        private readonly VisualElement _ticks;
        private readonly VisualElement _track;

        private double _duration;
        private double _end;
        private Target _dragging = Target.None;
        private double _playheadTime;
        private double _start;

        public TrimTimeline()
        {
            AddToClassList("irs-timeline");

            _track = new VisualElement { pickingMode = PickingMode.Ignore };
            _track.AddToClassList("irs-timeline__track");
            Add(_track);

            _ticks = new VisualElement { pickingMode = PickingMode.Ignore };
            _ticks.AddToClassList("irs-timeline__ticks");
            Add(_ticks);

            _range = new VisualElement { pickingMode = PickingMode.Ignore };
            _range.AddToClassList("irs-timeline__range");
            Add(_range);

            _startHandle = CreateHandle("irs-timeline__handle--start");
            _endHandle = CreateHandle("irs-timeline__handle--end");

            _playhead = new VisualElement { pickingMode = PickingMode.Ignore };
            _playhead.AddToClassList("irs-timeline__playhead");
            var knob = new VisualElement { pickingMode = PickingMode.Ignore };
            knob.AddToClassList("irs-timeline__knob");
            _playhead.Add(knob);
            Add(_playhead);

            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag());
        }

        /// <summary>Whether the user is dragging the knob, during which the owner should not move it.</summary>
        public bool IsDraggingPlayhead => _dragging == Target.Playhead;

        public event Action<double> StartDragged;
        public event Action<double> EndDragged;
        public event Action<double> PlayheadDragged;

        /// <summary>Raised when the user releases the knob.</summary>
        public event Action PlayheadReleased;

        public void SetDuration(double duration, IReadOnlyList<double> keyFrames)
        {
            _duration = Math.Max(0, duration);
            _ticks.Clear();
            if (keyFrames != null)
                foreach (var time in keyFrames)
                {
                    var tick = new VisualElement { pickingMode = PickingMode.Ignore };
                    tick.AddToClassList("irs-timeline__tick");
                    tick.style.left = Percent(time);
                    _ticks.Add(tick);
                }

            Layout();
        }

        public void SetRange(double start, double end)
        {
            _start = start;
            _end = end;
            Layout();
        }

        public void SetPlayhead(double time)
        {
            _playheadTime = time;
            _playhead.style.left = Percent(time);
        }

        private VisualElement CreateHandle(string modifier)
        {
            var handle = new VisualElement { pickingMode = PickingMode.Ignore };
            handle.AddToClassList("irs-timeline__handle");
            handle.AddToClassList(modifier);
            Add(handle);
            return handle;
        }

        private void Layout()
        {
            _startHandle.style.left = Percent(_start);
            _endHandle.style.left = Percent(_end);
            _range.style.left = Percent(_start);
            _range.style.width = Length.Percent((float)(Fraction(_end) - Fraction(_start)) * 100);
            _playhead.style.left = Percent(_playheadTime);
        }

        private void OnPointerDown(PointerDownEvent e)
        {
            if (_duration <= 0 || !enabledInHierarchy) return;

            var x = e.localPosition.x;
            var startDistance = Mathf.Abs(x - XOf(_start));
            var endDistance = Mathf.Abs(x - XOf(_end));
            var playheadDistance = Mathf.Abs(x - XOf(_playheadTime));

            // The knob wins ties, so that it stays reachable when it sits on a handle.
            if (playheadDistance <= HandleGrabRadius && playheadDistance <= Mathf.Min(startDistance, endDistance))
                _dragging = Target.Playhead;
            else if (startDistance <= HandleGrabRadius && startDistance <= endDistance)
                _dragging = Target.Start;
            else if (endDistance <= HandleGrabRadius)
                _dragging = Target.End;
            else
                // A click elsewhere on the track moves the playback position there.
                _dragging = Target.Playhead;

            this.CapturePointer(e.pointerId);
            Raise(TimeOf(x));
            e.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (_dragging == Target.None || !this.HasPointerCapture(e.pointerId)) return;
            Raise(TimeOf(e.localPosition.x));
            e.StopPropagation();
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (!this.HasPointerCapture(e.pointerId)) return;
            this.ReleasePointer(e.pointerId);
            EndDrag();
            e.StopPropagation();
        }

        private void EndDrag()
        {
            var wasPlayhead = _dragging == Target.Playhead;
            _dragging = Target.None;
            if (wasPlayhead) PlayheadReleased?.Invoke();
        }

        private void Raise(double time)
        {
            switch (_dragging)
            {
                case Target.Start:
                    StartDragged?.Invoke(time);
                    break;
                case Target.End:
                    EndDragged?.Invoke(time);
                    break;
                case Target.Playhead:
                    PlayheadDragged?.Invoke(time);
                    break;
            }
        }

        private double Fraction(double time)
        {
            return _duration > 0 ? Math.Max(0, Math.Min(1, time / _duration)) : 0;
        }

        private Length Percent(double time)
        {
            return Length.Percent((float)Fraction(time) * 100);
        }

        private float XOf(double time)
        {
            return (float)Fraction(time) * contentRect.width;
        }

        private double TimeOf(float x)
        {
            var width = contentRect.width;
            return width > 0 ? Math.Max(0, Math.Min(1, x / width)) * _duration : 0;
        }

        private enum Target
        {
            None,
            Start,
            End,
            Playhead
        }
    }
}
