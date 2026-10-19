// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.UIElements;
using PointerType = UnityEngine.UIElements.PointerType;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Tap handling that responds when the pointer is released over the element, and not when a touch moves far
    ///     enough to be a drag, so that dragging over a control scrolls the enclosing scroll view instead of activating
    ///     the control.
    /// </summary>
    /// <remarks>
    ///     The standard controls do not behave this way on their own: drop-down fields open when the pointer is pressed,
    ///     and buttons and toggles capture the pointer when it is pressed, which keeps a scroll view from scrolling in
    ///     Unity 2022.3, where events sent to a capturing element do not reach its ancestors. These controls are
    ///     therefore given a tap handler that intercepts the pointer events before the control handles them, and
    ///     activates the control with a navigation submit event, the event that the keyboard and gamepads use.
    ///     Text fields take the focus, and open the touch screen keyboard, when a touch is pressed. They are given a
    ///     handler that defers the focus to the release of a tap, and leaves the pointer events of a mouse, which
    ///     select text, to the field.
    /// </remarks>
    internal static class Taps
    {
        public const string PressedClassName = "irs-pressed";
        private const string HandledClassName = "irs-tap";

        /// <summary>Calls <paramref name="tapped" /> when the element is tapped or clicked.</summary>
        public static T OnTap<T>(this T element, Action tapped) where T : VisualElement
        {
            element.AddToClassList(HandledClassName);
            element.AddManipulator(new TapManipulator(tapped));
            return element;
        }

        /// <summary>
        ///     Gives tap handling to the buttons, toggles, drop-down fields and text fields in a subtree that do not have it
        ///     yet. Call it again after adding such controls.
        /// </summary>
        public static void Apply(VisualElement root)
        {
            root.Query<VisualElement>().ForEach(element =>
            {
                if (element.ClassListContains(HandledClassName)) return;
                if (element is Button || element is BaseBoolField || element is DropdownField)
                {
                    element.OnTap(() => Submit(element));
                }
                else if (element.ClassListContains(TextInputBaseField<string>.ussClassName))
                {
                    element.AddToClassList(HandledClassName);
                    element.AddManipulator(new TextFieldTapManipulator(() => Submit(element)));
                }
            });
        }

        private static void Submit(VisualElement control)
        {
            using var evt = NavigationSubmitEvent.GetPooled();
            evt.target = control;
            control.SendEvent(evt);
        }

        private sealed class TapManipulator : PointerManipulator
        {
            // In panel units. Smaller than the threshold at which a scroll view starts scrolling, so that the tap is
            // cancelled before the scroll view takes the pointer.
            private const float DragThreshold = 8;

            private readonly Action _tapped;
            private int _pointerId = PointerId.invalidPointerId;
            private Vector2 _start;

            public TapManipulator(Action tapped)
            {
                _tapped = tapped;
                activators.Add(new ManipulatorActivationFilter { button = MouseButton.LeftMouse });
            }

            protected override void RegisterCallbacksOnTarget()
            {
                // The trickle-down phase reaches the target before the handlers of the control itself.
                target.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerCancelEvent>(OnPointerCancel, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerLeaveEvent>(OnPointerLeave);
                target.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
                target.RegisterCallback<MouseDownEvent>(StopMouseEvent, TrickleDown.TrickleDown);
                target.RegisterCallback<MouseUpEvent>(StopMouseEvent, TrickleDown.TrickleDown);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                target.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                target.UnregisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
                target.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                target.UnregisterCallback<PointerCancelEvent>(OnPointerCancel, TrickleDown.TrickleDown);
                target.UnregisterCallback<PointerLeaveEvent>(OnPointerLeave);
                target.UnregisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
                target.UnregisterCallback<MouseDownEvent>(StopMouseEvent, TrickleDown.TrickleDown);
                target.UnregisterCallback<MouseUpEvent>(StopMouseEvent, TrickleDown.TrickleDown);
            }

            private bool IsTracking => _pointerId != PointerId.invalidPointerId;

            private void OnPointerDown(PointerDownEvent evt)
            {
                // The event is stopped even when no tap starts, so that the control never handles it by itself.
                evt.StopImmediatePropagation();
                if (IsTracking || !CanStartManipulation(evt) || !target.enabledInHierarchy) return;

                _pointerId = evt.pointerId;
                _start = evt.position;
                target.AddToClassList(PressedClassName);
                // A mouse cannot scroll by dragging, so it keeps the pointer to tell a release outside the element.
                // A touch leaves the pointer free for a scroll view to take.
                if (evt.pointerType == PointerType.mouse) target.CapturePointer(evt.pointerId);
            }

            private void OnPointerMove(PointerMoveEvent evt)
            {
                if (evt.pointerId != _pointerId) return;

                if (evt.pointerType == PointerType.mouse)
                {
                    target.EnableInClassList(PressedClassName, target.worldBound.Contains(evt.position));
                    return;
                }

                if (((Vector2)evt.position - _start).sqrMagnitude > DragThreshold * DragThreshold) Cancel();
            }

            private void OnPointerUp(PointerUpEvent evt)
            {
                evt.StopImmediatePropagation();
                if (evt.pointerId != _pointerId) return;

                var inside = target.worldBound.Contains(evt.position);
                Cancel();
                if (inside && target.enabledInHierarchy) _tapped();
            }

            private void OnPointerCancel(PointerCancelEvent evt)
            {
                if (evt.pointerId == _pointerId) Cancel();
            }

            private void OnPointerLeave(PointerLeaveEvent evt)
            {
                if (evt.pointerId == _pointerId && evt.pointerType != PointerType.mouse) Cancel();
            }

            private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
            {
                if (evt.pointerId == _pointerId) Cancel();
            }

            // Mouse events emulated from pointer events would otherwise start the handling of the control.
            private static void StopMouseEvent(EventBase evt)
            {
                evt.StopImmediatePropagation();
            }

            private void Cancel()
            {
                var pointerId = _pointerId;
                _pointerId = PointerId.invalidPointerId;
                target.RemoveFromClassList(PressedClassName);
                if (pointerId != PointerId.invalidPointerId && target.HasPointerCapture(pointerId))
                    target.ReleasePointer(pointerId);
            }
        }

        private sealed class TextFieldTapManipulator : Manipulator
        {
            private const float DragThreshold = 8;

            private readonly Action _tapped;
            private int _pointerId = PointerId.invalidPointerId;
            private bool _stopMouseUp;
            private Vector2 _start;

            public TextFieldTapManipulator(Action tapped)
            {
                _tapped = tapped;
            }

            protected override void RegisterCallbacksOnTarget()
            {
                target.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerCancelEvent>(OnPointerCancel, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerLeaveEvent>(OnPointerLeave);
                target.RegisterCallback<MouseDownEvent>(OnMouseDown, TrickleDown.TrickleDown);
                target.RegisterCallback<MouseUpEvent>(OnMouseUp, TrickleDown.TrickleDown);
            }

            protected override void UnregisterCallbacksFromTarget()
            {
                target.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                target.UnregisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
                target.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
                target.UnregisterCallback<PointerCancelEvent>(OnPointerCancel, TrickleDown.TrickleDown);
                target.UnregisterCallback<PointerLeaveEvent>(OnPointerLeave);
                target.UnregisterCallback<MouseDownEvent>(OnMouseDown, TrickleDown.TrickleDown);
                target.UnregisterCallback<MouseUpEvent>(OnMouseUp, TrickleDown.TrickleDown);
            }

            private bool IsTracking => _pointerId != PointerId.invalidPointerId;

            private void OnPointerDown(PointerDownEvent evt)
            {
                _stopMouseUp = false;
                // A mouse selects text, and a field that already has the focus places the cursor where it is touched.
                if (evt.pointerType == PointerType.mouse || IsTracking || HasFocusWithin()) return;

                evt.StopImmediatePropagation();
                IgnoreFocus(evt);
                if (evt.button != (int)MouseButton.LeftMouse || !target.enabledInHierarchy) return;

                _pointerId = evt.pointerId;
                _start = evt.position;
            }

            // The panel focuses the pressed element after the event has been handled, or the field when the element
            // cannot take the focus, such as the label of the field.
            private void IgnoreFocus(PointerDownEvent evt)
            {
#if UNITY_2023_2_OR_NEWER
                target.focusController?.IgnoreEvent(evt);
#else
                // Unity 2022.3 changes the focus in the default action of the event.
                evt.PreventDefault();
#endif
            }

            private void OnPointerMove(PointerMoveEvent evt)
            {
                if (evt.pointerId != _pointerId) return;
                if (((Vector2)evt.position - _start).sqrMagnitude > DragThreshold * DragThreshold) Cancel();
            }

            private void OnPointerUp(PointerUpEvent evt)
            {
                if (evt.pointerId != _pointerId) return;

                evt.StopImmediatePropagation();
                _stopMouseUp = true;
                var inside = target.worldBound.Contains(evt.position);
                Cancel();
                if (inside && target.enabledInHierarchy) _tapped();
            }

            private void OnPointerCancel(PointerCancelEvent evt)
            {
                if (evt.pointerId == _pointerId) Cancel();
            }

            private void OnPointerLeave(PointerLeaveEvent evt)
            {
                if (evt.pointerId == _pointerId) Cancel();
            }

            // Mouse events emulated from the pointer events of a tap would otherwise start a text selection.
            private void OnMouseDown(MouseDownEvent evt)
            {
                if (IsTracking) evt.StopImmediatePropagation();
            }

            private void OnMouseUp(MouseUpEvent evt)
            {
                if (!IsTracking && !_stopMouseUp) return;
                _stopMouseUp = false;
                evt.StopImmediatePropagation();
            }

            private bool HasFocusWithin()
            {
                // The focus controller reports the field, not the text element in it, as focused.
                return target.focusController?.focusedElement is VisualElement focused &&
                       (focused == target || target.Contains(focused));
            }

            private void Cancel()
            {
                _pointerId = PointerId.invalidPointerId;
            }
        }
    }
}
