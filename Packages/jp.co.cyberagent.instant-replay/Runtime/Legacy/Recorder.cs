// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Captures provided frames and saves them to the specified directory.
    /// </summary>
    internal class Recorder : IDisposable
    {
        private readonly string _directory;
        private readonly bool _disposeFrameProvider;
        private readonly double? _fixedFrameInterval;
        private readonly FramePreprocessor _framePreprocessor;
        private readonly IFrameProvider _frameProvider;
        private readonly Action<double> _onDiscardFrame;
        private readonly Slot[] _slots;
        private int _currentSlot;
        private ulong _frame;
        private double _frameTimer;
        private int _numBusySlots;
        private Action<RecorderResult> _onCompleted;
        private double _prevFrameTime;

        public Recorder(int numFrames,
            double? fixedFrameRate,
            string directory,
            IFrameProvider frameProvider,
            bool disposeFrameProvider,
            FramePreprocessor framePreprocessor,
            Action<double /* keepSeconds */> onDiscardFrame,
            Action<RecorderResult> onCompleted)
        {
            _frameProvider = frameProvider;
            _framePreprocessor = framePreprocessor;
            _onDiscardFrame = onDiscardFrame;
            _onCompleted = onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));
            _disposeFrameProvider = disposeFrameProvider;
            _fixedFrameInterval = 1.0 / fixedFrameRate;

            // init slots
            _slots = new Slot[numFrames];

            directory = Path.GetFullPath(directory);

            // NOTE: Path.GetFullPath produces too much garbage, so we ensure the path is transformed to full path here instead of calling Path.GetFullPath every time we write to the file.
            for (var i = 0; i < _slots.Length; i++)
                _slots[i] = new Slot(Path.Combine(directory, $"{i}.jpg"));

            frameProvider.OnFrameProvided += OnFrameProvided;
        }

        public int NumBusySlots => _numBusySlots;

        public void Dispose()
        {
            var onCompleted = Interlocked.Exchange(ref _onCompleted, null);
            if (onCompleted == null) return;
            _frameProvider.OnFrameProvided -= OnFrameProvided;

            // wait for all requests
            CheckIfCompleted(() =>
            {
                if (_disposeFrameProvider) _frameProvider.Dispose();
                _framePreprocessor.Dispose();

                var slots = _slots.Where(s => s._time > 0).OrderBy(s => s._time).ToArray();
                RecorderFrame[] frames;
                if (slots.Length == 0)
                {
                    frames = Array.Empty<RecorderFrame>();
                }
                else
                {
                    var startTime = slots.Min(s => s._time);
                    frames = slots.Select(s => new RecorderFrame(s._definiteFullPath, s._time - startTime)).ToArray();
                }

                var width = 0;
                var height = 0;
                foreach (var slot in slots)
                {
                    width = Mathf.Max(slot._width, width);
                    height = Mathf.Max(slot._height, height);
                }

                onCompleted(new RecorderResult(width, height, frames));
            });
        }

        private void OnFrameProvided(IFrameProvider.Frame frame)
        {
            var texture = frame.Texture;
            var time = frame.Timestamp;
            var dataStartsAtTop = frame.DataStartsAtTop;

            var deltaTime = time - _prevFrameTime;

            if (deltaTime <= 0) return;

            _frameTimer += deltaTime;
            _prevFrameTime = time;

            if (_fixedFrameInterval is { } fixedFrameInterval)
            {
                if (_frameTimer < _fixedFrameInterval) return;
                _frameTimer %= fixedFrameInterval;
            }

            var currentSlot = _currentSlot;

            ref var slot = ref _slots[currentSlot];

            if (Interlocked.CompareExchange(ref slot._isBusy, 1, 0) != 0)
            {
                ILogger.LogWarningCore(
                    "InstantReplay: Skipping a frame because the preserved slots have run out. Increasing the number of frames may help.");
                return;
            }

            Interlocked.Increment(ref _numBusySlots);

            var renderTexture = _framePreprocessor.Process(texture, dataStartsAtTop);

            // Notify the audio recorder to the old frames are discarded.
            if (slot._time > 0)
                _onDiscardFrame?.Invoke(time - slot._time);

            slot._processingTime = time;
            slot._width = renderTexture.width;
            slot._height = renderTexture.height;

            try
            {
                _ = new FrameReadbackRequest<(Recorder session, int currentSlot)>(renderTexture,
                    slot._definiteFullPath,
                    (this, currentSlot),
                    static (_, context, exception) =>
                    {
                        var (@this, currentSlot) = context;
                        ref var slot = ref @this._slots[currentSlot];
                        slot._isBusy = 0;
                        Interlocked.Decrement(ref @this._numBusySlots);

                        if (exception != null)
                        {
                            ILogger.LogExceptionCore(exception);
                            return;
                        }

                        slot._time = slot._processingTime;
                    });
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }

            _currentSlot++;
            _currentSlot %= _slots.Length;
        }

        private void CheckIfCompleted(Action onComplete)
        {
            SynchronizationContext.Current.Post(_ =>
            {
                if (_slots.Any(static slot => slot._isBusy != 0))
                    CheckIfCompleted(onComplete);
                else
                    onComplete?.Invoke();
            }, null);
        }

        private struct Slot
        {
            public readonly string _definiteFullPath;
            public int _isBusy;
            public double _time;
            public double _processingTime;
            public int _width;
            public int _height;

            public Slot(string definiteFullPath) : this()
            {
                _definiteFullPath = definiteFullPath;
            }
        }
    }

    internal class RecorderResult
    {
        public RecorderResult(int width, int height, RecorderFrame[] frames)
        {
            Width = width;
            Height = height;
            Frames = frames;
        }

        public int Width { get; }
        public int Height { get; }
        public RecorderFrame[] Frames { get; }
    }

    internal struct RecorderFrame
    {
        public string Path { get; }
        public double Time { get; }

        public RecorderFrame(string path, double time)
        {
            Path = path;
            Time = time;
        }
    }
}
