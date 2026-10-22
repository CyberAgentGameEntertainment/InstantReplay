// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace InstantReplay.Suite
{
    /// <summary>
    ///     Records gameplay according to <see cref="InstantReplaySuiteSettings" />: keeps the latest footage while
    ///     buffering, saves it as a replay on request, or records without a bound until stopped. Buffering and unbounded
    ///     recording are mutually exclusive; buffering is suspended while an unbounded recording runs.
    /// </summary>
    /// <remarks>
    ///     Every member must be called on the main thread, and every event is raised on it. The recorder owns at most one
    ///     frame provider and one audio sample provider at a time and reuses them across sessions, so that providers
    ///     that may exist only once are never created twice.
    /// </remarks>
    public sealed class InstantReplayRecorder : IDisposable
    {
        private const int MaxFaultsInWindow = 3;
        private const double FaultWindowSeconds = 10;
        private const double MinimumSecondsForMeasuredCapacity = 5;

        private readonly ProviderSlot<IAudioSampleProvider> _audioSlot;
        private readonly SynchronizationContext _context;
        private readonly Stopwatch _faultClock = Stopwatch.StartNew();
        private readonly List<double> _faultTimes = new();
        private readonly ProviderSlot<IFrameProvider> _frameSlot;
        private readonly int _mainThreadId;

        private bool _activated;
        private bool _disposed;
        private RecorderPreferences _preferences;
        private RealtimeInstantReplaySession _session;
        private PresetContext _sessionContext;
        private int _sessionGeneration;
        private bool _suspended;
        private ApplicationSuspensionObserver _suspensionObserver;
        private UnboundedRecordingSession _unboundedSession;

        /// <summary>
        ///     Creates a recorder. Nothing is recorded until <see cref="Activate" /> is called.
        /// </summary>
        /// <param name="settings">Project settings. Null uses the built-in defaults.</param>
        public InstantReplayRecorder(InstantReplaySuiteSettings settings)
        {
            _context = SynchronizationContext.Current ??
                       throw new InvalidOperationException("InstantReplayRecorder must be created on the main thread.");
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            Settings = settings != null ? settings : ScriptableObject.CreateInstance<InstantReplaySuiteSettings>();

            var presets = new List<RecordingPreset>();
            if (Settings.Presets != null)
                foreach (var preset in Settings.Presets)
                    if (preset != null)
                        presets.Add(preset);
            if (presets.Count == 0) presets.Add(RecordingPreset.CreateDefault());
            Presets = presets;
            ActivePreset = Settings.FindPreset(Settings.DefaultPresetId) ?? presets[0];

            _frameSlot = new ProviderSlot<IFrameProvider>(static () => new ScreenshotFrameProvider());
            _audioSlot = new ProviderSlot<IAudioSampleProvider>(static () => new UnityAudioSampleProvider());

            Library = new RecordingLibrary(Settings.ResolveOutputDirectory(), Settings.MaxRecordings,
                (long)Settings.MaxTotalMegabytes * 1024 * 1024);

            var shareTargets = new List<ShareTarget>();
            if (Settings.ShareTargets != null)
                foreach (var target in Settings.ShareTargets)
                    if (target != null)
                        shareTargets.Add(target);
            ShareTargets = shareTargets;
        }

        public InstantReplaySuiteSettings Settings { get; }

        public RecorderState State { get; private set; } = RecorderState.Idle;

        /// <summary>The latest error that interrupted recording or saving, or null.</summary>
        public Exception LastException { get; private set; }

        /// <summary>Presets the user can choose from. Never empty.</summary>
        public IReadOnlyList<RecordingPreset> Presets { get; }

        public RecordingPreset ActivePreset { get; private set; }

        /// <summary>
        ///     Whether <see cref="ActivePreset" /> is not one of <see cref="Presets" />, for example because the user has
        ///     customized it. Such a preset is persisted as a whole and restored on the next launch.
        /// </summary>
        public bool IsActivePresetCustomized => IndexOfPreset(ActivePreset) < 0;

        /// <summary>
        ///     Whether the user wants buffering. Buffering actually runs only while no replay is being saved and no
        ///     unbounded recording is running.
        /// </summary>
        public bool IsBufferingEnabled { get; private set; }

        /// <summary>
        ///     Whether the recording has been paused by <see cref="Pause" />. Recording is also paused while the
        ///     application is suspended, which this property does not reflect.
        /// </summary>
        public bool IsPaused { get; private set; }

        private bool IsSessionPaused => IsPaused || _suspended;

        /// <summary>The running unbounded recording, or null.</summary>
        public UnboundedRecording CurrentUnboundedRecording { get; private set; }

        public RecordingLibrary Library { get; }

        /// <summary>
        ///     Destinations offered when the user shares a recording, initialized from
        ///     <see cref="InstantReplaySuiteSettings.ShareTargets" />. Targets can be added at runtime, for example to
        ///     supply credentials that are not stored in the build.
        /// </summary>
        public IList<ShareTarget> ShareTargets { get; }

        public void Dispose()
        {
            if (_disposed) return;
            ThrowIfNotMainThread();
            _disposed = true;

            if (_suspensionObserver != null)
            {
                _suspensionObserver.DestroySelf();
                _suspensionObserver = null;
            }

            StopBuffering();

            if (_unboundedSession != null)
            {
                var session = _unboundedSession;
                _unboundedSession = null;
                DisposeQuietly(session);
                if (CurrentUnboundedRecording != null)
                {
                    CurrentUnboundedRecording.Fail(new ObjectDisposedException(nameof(InstantReplayRecorder)));
                    // The file cannot be finalized synchronously and would not be playable.
                    Library.Abandon(CurrentUnboundedRecording.OutputPath);
                    CurrentUnboundedRecording = null;
                }
            }

            _frameSlot.Dispose();
            _audioSlot.Dispose();
            State = RecorderState.Idle;
        }

        /// <summary>Raised when <see cref="State" /> changes.</summary>
        public event Action<RecorderState> StateChanged;

        /// <summary>Raised when the preset, the buffering toggle, or the pause state changes.</summary>
        public event Action ConfigurationChanged;

        /// <summary>
        ///     Restores the choices the user made in a previous launch and starts buffering if it is enabled. Calling it
        ///     again has no effect.
        /// </summary>
        public void Activate()
        {
            ThrowIfNotMainThread();
            ThrowIfDisposed();
            if (_activated) return;
            _activated = true;

            _preferences = Settings.PersistUserPreferences ? RecorderPreferences.Load() : new RecorderPreferences();
            var preset = LoadCustomPreset(_preferences.customPreset) ?? FindPreset(_preferences.selectedPresetId);
            if (preset != null) ActivePreset = preset;
            IsBufferingEnabled = _preferences.hasBufferingEnabled
                ? _preferences.bufferingEnabled
                : Settings.StartBufferingOnLaunch;

            // Providers keep their clocks running while the application is suspended, so the suspension would appear as
            // a long period without frames unless recording is paused meanwhile.
            if (Application.isPlaying)
                _suspensionObserver = ApplicationSuspensionObserver.Create(OnApplicationSuspensionChanged);

            if (IsBufferingEnabled) StartBuffering();
            ConfigurationChanged?.Invoke();
        }

        /// <summary>
        ///     Switches to the preset with the given identifier.
        /// </summary>
        /// <exception cref="ArgumentException">No preset has the identifier.</exception>
        /// <exception cref="InvalidOperationException">A replay is being saved or an unbounded recording is running.</exception>
        public void ApplyPreset(string presetId)
        {
            var preset = FindPreset(presetId) ??
                         throw new ArgumentException($"No preset has the identifier '{presetId}'.", nameof(presetId));
            ApplyPreset(preset);
        }

        /// <summary>
        ///     Switches to the given preset, which need not be one of <see cref="Presets" />. Buffering restarts with the
        ///     new parameters, discarding the footage buffered so far. Changes made to the preset object afterwards take
        ///     effect only when it is applied again; to customize a preset, apply a modified
        ///     <see cref="RecordingPreset.Clone" />.
        /// </summary>
        /// <exception cref="InvalidOperationException">A replay is being saved or an unbounded recording is running.</exception>
        public void ApplyPreset(RecordingPreset preset)
        {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            ThrowIfNotMainThread();
            ThrowIfDisposed();
            ThrowIfBusy("change the preset");
            EnsureActivated();

            ActivePreset = preset;
            SavePreferences();
            if (_session != null)
            {
                StopBuffering();
                StartBuffering();
            }

            ConfigurationChanged?.Invoke();
        }

        /// <summary>
        ///     Enables or disables buffering. Enabling it while <see cref="RecorderState.Faulted" /> retries. While a replay
        ///     is being saved or an unbounded recording is running, the choice takes effect when that finishes.
        /// </summary>
        public void SetBufferingEnabled(bool enabled)
        {
            ThrowIfNotMainThread();
            ThrowIfDisposed();
            EnsureActivated();

            var retry = enabled && State == RecorderState.Faulted;
            if (IsBufferingEnabled == enabled && !retry) return;

            IsBufferingEnabled = enabled;
            SavePreferences();

            if (State is not (RecorderState.Exporting or RecorderState.RecordingUnbounded))
            {
                if (enabled)
                {
                    _faultTimes.Clear();
                    LastException = null;
                    StartBuffering();
                }
                else
                {
                    StopBuffering();
                    SetState(RecorderState.Idle);
                }
            }

            ConfigurationChanged?.Invoke();
        }

        /// <summary>
        ///     Pauses buffering or the unbounded recording. Paused time is excluded from the footage.
        /// </summary>
        /// <remarks>
        ///     The recorder also pauses by itself while the application is suspended, independently of this method.
        /// </remarks>
        public void Pause()
        {
            SetPaused(true);
        }

        public void Resume()
        {
            SetPaused(false);
        }

        /// <summary>
        ///     Returns a snapshot for display.
        /// </summary>
        public RecorderStatistics GetStatistics()
        {
            ThrowIfNotMainThread();

            if (CurrentUnboundedRecording != null)
                return new RecorderStatistics(0, 0, 0, 0, CurrentUnboundedRecording.ElapsedSeconds);

            if (_session == null)
            {
                var context = PresetContext.FromCurrentEnvironment();
                return new RecorderStatistics(0, ActivePreset.EstimateRecordableDurationSeconds(context), 0,
                    ActivePreset.Buffer.GetCapacityBytes(), 0);
            }

            var buffer = _session.GetBufferStatistics();
            var estimate = buffer is { BufferedDurationSeconds: >= MinimumSecondsForMeasuredCapacity, UsedBytes: > 0 }
                ? buffer.CapacityBytes * buffer.BufferedDurationSeconds / buffer.UsedBytes
                : ActivePreset.EstimateRecordableDurationSeconds(_sessionContext);
            return new RecorderStatistics(buffer.BufferedDurationSeconds, estimate, buffer.UsedBytes,
                buffer.CapacityBytes, 0);
        }

        /// <summary>
        ///     Saves the buffered footage to the library. Buffering is suspended while the file is written and restarts
        ///     with an empty buffer afterwards.
        /// </summary>
        /// <exception cref="InvalidOperationException">The recorder is not buffering.</exception>
        public async ValueTask<RecordingEntry> SaveReplayAsync(ReplaySaveRequest request = null)
        {
            ThrowIfNotMainThread();
            ThrowIfDisposed();
            if (State != RecorderState.Buffering || _session == null)
                throw new InvalidOperationException($"Cannot save a replay while the recorder is {State}.");

            var session = DetachSession();
            SetState(RecorderState.Exporting);

            var createdAtUtc = DateTime.UtcNow;
            var path = Library.CreateOutputPath(RecordingSource.Replay);
            var completed = false;
            try
            {
                var buffered = session.GetBufferStatistics().BufferedDurationSeconds;
                var duration = request?.DurationSeconds is { } requested ? Math.Min(requested, buffered) : buffered;

                await session.StopAndExportAsync(request?.DurationSeconds, path);
                completed = true;
                return Library.Add(path, RecordingSource.Replay, createdAtUtc, duration, request?.Metadata);
            }
            catch (Exception ex)
            {
                LastException = ex;
                throw;
            }
            finally
            {
                DisposeQuietly(session);
                if (!completed) Library.Abandon(path);
                if (!_disposed) ResumeAfterRecording();
            }
        }

        /// <summary>
        ///     Takes the buffered footage out of the buffer without saving it, so that the user can preview it and save
        ///     only part of it. Buffering restarts with an empty buffer as soon as the footage has been taken. The caller
        ///     owns the returned draft and must dispose it.
        /// </summary>
        /// <param name="request">
        ///     <see cref="ReplaySaveRequest.DurationSeconds" /> limits the footage taken, and
        ///     <see cref="ReplaySaveRequest.Metadata" /> is stored with every recording saved from the draft.
        /// </param>
        /// <exception cref="InvalidOperationException">The recorder is not buffering, or the buffer holds no footage.</exception>
        public async ValueTask<ReplayDraft> CaptureReplayAsync(ReplaySaveRequest request = null)
        {
            ThrowIfNotMainThread();
            ThrowIfDisposed();
            if (State != RecorderState.Buffering || _session == null)
                throw new InvalidOperationException($"Cannot capture a replay while the recorder is {State}.");

            var session = DetachSession();
            SetState(RecorderState.Exporting);

            try
            {
                var capture = await session.StopAndCaptureAsync(request?.DurationSeconds);
                return new ReplayDraft(Library, capture, DateTime.UtcNow, request?.Metadata);
            }
            catch (Exception ex)
            {
                LastException = ex;
                throw;
            }
            finally
            {
                DisposeQuietly(session);
                if (!_disposed) ResumeAfterRecording();
            }
        }

        /// <summary>
        ///     Starts recording to a file in the library until <see cref="UnboundedRecording.StopAsync" /> is called.
        ///     Buffering is suspended meanwhile and the footage buffered so far is discarded.
        /// </summary>
        /// <exception cref="InvalidOperationException">A replay is being saved or an unbounded recording is running.</exception>
        public UnboundedRecording StartUnbounded(UnboundedRecordingRequest request = null)
        {
            ThrowIfNotMainThread();
            ThrowIfDisposed();
            ThrowIfBusy("start an unbounded recording");
            EnsureActivated();

            StopBuffering();

            var path = Library.CreateOutputPath(RecordingSource.Unbounded);
            var options = ActivePreset.ToEncodingOptions(PresetContext.FromCurrentEnvironment());
            options.DiskBuffer = null;

            var generation = ++_sessionGeneration;
            try
            {
                _unboundedSession = new UnboundedRecordingSession(path, options, _frameSlot.Get(), false,
                    _audioSlot.Get(), false, ex => Post(() => OnSessionException(generation, ex)));
            }
            catch (Exception ex)
            {
                LastException = ex;
                Library.Abandon(path);
                ResumeAfterRecording();
                throw;
            }

            if (IsSessionPaused) _unboundedSession.Pause();
            CurrentUnboundedRecording =
                new UnboundedRecording(this, path, DateTime.UtcNow, request?.Metadata, IsSessionPaused);
            SetState(RecorderState.RecordingUnbounded);
            return CurrentUnboundedRecording;
        }

        /// <summary>
        ///     Creates frame providers with <paramref name="factory" /> from now on. Null restores the default, which
        ///     captures the screen. The current provider is disposed before the factory is called.
        /// </summary>
        /// <exception cref="InvalidOperationException">A replay is being saved or an unbounded recording is running.</exception>
        public void SetFrameProviderFactory(Func<IFrameProvider> factory)
        {
            ReplaceProvider(() => _frameSlot.SetFactory(factory));
        }

        /// <summary>
        ///     Uses the given frame provider. The recorder disposes it later only when <paramref name="takeOwnership" /> is
        ///     true.
        /// </summary>
        /// <exception cref="InvalidOperationException">A replay is being saved or an unbounded recording is running.</exception>
        public void SetFrameProvider(IFrameProvider provider, bool takeOwnership)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            ReplaceProvider(() => _frameSlot.SetInstance(provider, takeOwnership));
        }

        /// <summary>
        ///     Creates audio sample providers with <paramref name="factory" /> from now on. Null restores the default,
        ///     which captures the output of the active <see cref="AudioListener" />. The current provider is disposed before
        ///     the factory is called.
        /// </summary>
        /// <exception cref="InvalidOperationException">A replay is being saved or an unbounded recording is running.</exception>
        public void SetAudioSampleProviderFactory(Func<IAudioSampleProvider> factory)
        {
            ReplaceProvider(() => _audioSlot.SetFactory(factory));
        }

        /// <summary>
        ///     Uses the given audio sample provider. The recorder disposes it later only when
        ///     <paramref name="takeOwnership" /> is true.
        /// </summary>
        /// <exception cref="InvalidOperationException">A replay is being saved or an unbounded recording is running.</exception>
        public void SetAudioSampleProvider(IAudioSampleProvider provider, bool takeOwnership)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            ReplaceProvider(() => _audioSlot.SetInstance(provider, takeOwnership));
        }

        /// <summary>
        ///     Recreates the frame provider, for example after the object it captures has been destroyed. A provider
        ///     supplied by the caller is kept and only resubscribed.
        /// </summary>
        /// <exception cref="InvalidOperationException">A replay is being saved or an unbounded recording is running.</exception>
        public void InvalidateFrameProvider()
        {
            ReplaceProvider(_frameSlot.Invalidate);
        }

        /// <summary>
        ///     Recreates the audio sample provider, for example after the <see cref="AudioListener" /> has changed. A
        ///     provider supplied by the caller is kept and only resubscribed.
        /// </summary>
        /// <exception cref="InvalidOperationException">A replay is being saved or an unbounded recording is running.</exception>
        public void InvalidateAudioSampleProvider()
        {
            ReplaceProvider(_audioSlot.Invalidate);
        }

        internal async Task<RecordingEntry> StopUnboundedAsync(UnboundedRecording recording)
        {
            ThrowIfNotMainThread();
            if (!ReferenceEquals(recording, CurrentUnboundedRecording) || _unboundedSession == null)
                throw new InvalidOperationException("The recording is no longer running.");

            var session = _unboundedSession;
            _unboundedSession = null;
            _sessionGeneration++;
            recording.MarkStopped();
            SetState(RecorderState.Exporting);

            var completed = false;
            try
            {
                await session.CompleteAsync();
                completed = true;
                return Library.Add(recording.OutputPath, RecordingSource.Unbounded, recording.StartedAtUtc,
                    recording.ElapsedSeconds, recording.Metadata);
            }
            catch (Exception ex)
            {
                LastException = ex;
                throw;
            }
            finally
            {
                DisposeQuietly(session);
                if (!completed) Library.Abandon(recording.OutputPath);
                CurrentUnboundedRecording = null;
                if (!_disposed) ResumeAfterRecording();
            }
        }

        private void SetPaused(bool paused)
        {
            ThrowIfNotMainThread();
            ThrowIfDisposed();
            if (IsPaused == paused) return;
            var wasSessionPaused = IsSessionPaused;
            IsPaused = paused;
            ApplySessionPause(wasSessionPaused);
            ConfigurationChanged?.Invoke();
        }

        private void OnApplicationSuspensionChanged(bool suspended)
        {
            if (_disposed || _suspended == suspended) return;
            var wasSessionPaused = IsSessionPaused;
            _suspended = suspended;
            ApplySessionPause(wasSessionPaused);
        }

        private void ApplySessionPause(bool wasSessionPaused)
        {
            var paused = IsSessionPaused;
            if (paused == wasSessionPaused) return;

            if (paused)
            {
                _session?.Pause();
                _unboundedSession?.Pause();
            }
            else
            {
                _session?.Resume();
                _unboundedSession?.Resume();
            }

            CurrentUnboundedRecording?.SetPaused(paused);
        }

        private void StartBuffering()
        {
            if (_session != null) return;

            try
            {
                var context = PresetContext.FromCurrentEnvironment();
                var options = ActivePreset.ToEncodingOptions(context);
                var generation = ++_sessionGeneration;
                _session = new RealtimeInstantReplaySession(options, _frameSlot.Get(), false, _audioSlot.Get(), false,
                    ex => Post(() => OnSessionException(generation, ex)));
                _sessionContext = context;
                if (IsSessionPaused) _session.Pause();
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
                HandleBufferingFault(ex);
                return;
            }

            SetState(RecorderState.Buffering);
        }

        private void StopBuffering()
        {
            var session = DetachSession();
            if (session != null) DisposeQuietly(session);
        }

        private RealtimeInstantReplaySession DetachSession()
        {
            var session = _session;
            _session = null;
            // Exceptions reported by the detached session no longer concern the recorder.
            _sessionGeneration++;
            return session;
        }

        private void ResumeAfterRecording()
        {
            if (IsBufferingEnabled) StartBuffering();
            else SetState(RecorderState.Idle);
        }

        private void OnSessionException(int generation, Exception exception)
        {
            if (_disposed || generation != _sessionGeneration) return;
            ILogger.LogExceptionCore(exception);

            if (_unboundedSession != null)
            {
                var session = _unboundedSession;
                _unboundedSession = null;
                _sessionGeneration++;
                DisposeQuietly(session);
                LastException = exception;
                if (CurrentUnboundedRecording != null)
                {
                    CurrentUnboundedRecording.Fail(exception);
                    // The file was not finalized and cannot be played.
                    Library.Abandon(CurrentUnboundedRecording.OutputPath);
                    CurrentUnboundedRecording = null;
                }

                ResumeAfterRecording();
                return;
            }

            StopBuffering();
            HandleBufferingFault(exception);
        }

        private void HandleBufferingFault(Exception exception)
        {
            LastException = exception;

            var now = _faultClock.Elapsed.TotalSeconds;
            _faultTimes.Add(now);
            _faultTimes.RemoveAll(t => now - t > FaultWindowSeconds);
            if (_faultTimes.Count >= MaxFaultsInWindow)
            {
                SetState(RecorderState.Faulted);
                return;
            }

            // The provider may be the cause, for example when the object it captures has been destroyed.
            _frameSlot.Invalidate();
            _audioSlot.Invalidate();
            StartBuffering();
        }

        private void ReplaceProvider(Action replace)
        {
            ThrowIfNotMainThread();
            ThrowIfDisposed();
            ThrowIfBusy("change a provider");

            // The session is torn down first, so that the old provider has no subscriber when it is disposed and the
            // new one is created only after the old one is gone.
            var restart = _session != null;
            StopBuffering();
            replace();
            if (restart) StartBuffering();
        }

        private int IndexOfPreset(RecordingPreset preset)
        {
            for (var i = 0; i < Presets.Count; i++)
                if (ReferenceEquals(Presets[i], preset))
                    return i;
            return -1;
        }

        private static RecordingPreset LoadCustomPreset(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonUtility.FromJson<RecordingPreset>(json);
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
                return null;
            }
        }

        private RecordingPreset FindPreset(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var preset in Presets)
                if (preset.Id == id)
                    return preset;
            return null;
        }

        private void EnsureActivated()
        {
            if (!_activated) Activate();
        }

        private void SavePreferences()
        {
            if (_preferences == null || !Settings.PersistUserPreferences) return;
            _preferences.selectedPresetId = ActivePreset.Id;
            _preferences.customPreset = IsActivePresetCustomized ? JsonUtility.ToJson(ActivePreset) : null;
            _preferences.hasBufferingEnabled = true;
            _preferences.bufferingEnabled = IsBufferingEnabled;
            _preferences.Save();
        }

        private void SetState(RecorderState state)
        {
            if (State == state) return;
            State = state;
            StateChanged?.Invoke(state);
        }

        private void Post(Action action)
        {
            _context.Post(static state => ((Action)state)(), action);
        }

        private void ThrowIfBusy(string operation)
        {
            if (State is RecorderState.Exporting or RecorderState.RecordingUnbounded)
                throw new InvalidOperationException($"Cannot {operation} while the recorder is {State}.");
        }

        private void ThrowIfNotMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                throw new InvalidOperationException("InstantReplayRecorder must be used on the main thread.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(InstantReplayRecorder));
        }

        private static void DisposeQuietly(IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                ILogger.LogExceptionCore(ex);
            }
        }
    }
}
