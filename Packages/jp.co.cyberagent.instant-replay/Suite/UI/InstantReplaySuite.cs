// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace InstantReplay.Suite.UI
{
    /// <summary>Corner of the screen where <see cref="InstantReplaySuite" /> places the overlay.</summary>
    public enum OverlayCorner
    {
        TopRight,
        TopLeft,
        BottomRight,
        BottomLeft
    }

    /// <summary>
    ///     Drop-in recorder interface: an always-visible overlay that shows the recording status and opens a panel for
    ///     buffering, saving replays, unbounded recording, and preset selection. Place one in the first scene.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Instant Replay/Instant Replay Suite")]
    public sealed class InstantReplaySuite : MonoBehaviour
    {
        private const string PackagePath = "Packages/jp.co.cyberagent.instant-replay/Suite/";

        // One unit of the style sheet corresponds to 1/96 inch on desktops, following the CSS pixel, and to 1/160 inch on
        // handheld devices, following the Android density-independent pixel and close to the iOS point.
        private const float DesktopReferenceDpi = 96;
        private const float HandheldReferenceDpi = 160;

        [SerializeField] private InstantReplaySuiteSettings settings;
        [SerializeField] private ThemeStyleSheet themeStyleSheet;
        [SerializeField] private StyleSheet styleSheet;
        [SerializeField] private int sortingOrder = 1000;
        [SerializeField] private bool dontDestroyOnLoad = true;
        [SerializeField] private bool showOverlayOnLaunch = true;

        [Tooltip("Whether tapping the overlay opens the panel. Turn it off to show only the recording status.")]
        [SerializeField]
        private bool overlayOpensPanel = true;

        [Tooltip("Corner of the screen where the overlay is placed until the user chooses another one in the panel.")]
        [SerializeField]
        private OverlayCorner overlayCorner = OverlayCorner.TopRight;

        [Tooltip("Language of the interface. Automatic follows the system language.")] [SerializeField]
        private SuiteLanguage language = SuiteLanguage.Automatic;

        [Tooltip("Multiplier applied to the physical size of the interface.")]
        [SerializeField] [Range(0.5f, 2f)] private float scale = 1f;

        private PanelSettings _panelSettings;
#if !UNITY_6000_0_OR_NEWER
        private SystemFontFallback _systemFontFallback;
#endif
        private SuitePreferences _preferences;
        private InstantReplayRecorder _recorder;
        private SuiteView _view;

        /// <summary>The active instance, or null.</summary>
        public static InstantReplaySuite Current { get; private set; }

        /// <summary>
        ///     The recorder driven by this interface. Created on first access; configure providers through it before
        ///     <c>Start</c> to avoid creating the default ones.
        /// </summary>
        public InstantReplayRecorder Recorder => _recorder ??= new InstantReplayRecorder(settings);

        public InstantReplaySuiteSettings Settings => settings;

        /// <summary>Whether the overlay is shown. The recorder keeps running while it is hidden.</summary>
        public bool IsOverlayVisible
        {
            get => _view != null && _view.IsVisible;
            set
            {
                showOverlayOnLaunch = value;
                if (_view != null) _view.IsVisible = value;
            }
        }

        /// <summary>
        ///     Whether tapping the overlay opens the panel. When false, the overlay only shows the recording status, and
        ///     taps pass through it. Turn it off when the application controls the recorder with its own interface.
        /// </summary>
        public bool OverlayOpensPanel
        {
            get => overlayOpensPanel;
            set
            {
                overlayOpensPanel = value;
                if (_view != null) _view.OverlayOpensPanel = value;
            }
        }

        /// <summary>
        ///     Corner of the screen where the overlay is placed. The panel opens next to the overlay. The user can choose
        ///     another corner in the panel, and that choice is restored at the next launch when
        ///     <see cref="InstantReplaySuiteSettings.PersistUserPreferences" /> is true. Setting it from code does not
        ///     overwrite the stored choice.
        /// </summary>
        public OverlayCorner OverlayCorner
        {
            get => overlayCorner;
            set
            {
                if (!Enum.IsDefined(typeof(OverlayCorner), value))
                    throw new ArgumentOutOfRangeException(nameof(value), value, null);
                overlayCorner = value;
                if (_view != null) _view.OverlayCorner = value;
            }
        }

        /// <summary>
        ///     Shows the trim view for a draft and returns the range and the thumbnail chosen by the user, without saving
        ///     anything. Use it to let the user choose the footage to attach to a report, and write the footage with
        ///     <see cref="ReplayDraft.ExportAsync(ReplaySelection, string)" /> or
        ///     <see cref="ReplayDraft.SaveAsync(ReplaySelection)" />.
        /// </summary>
        /// <remarks>
        ///     The view is shown even while the overlay is hidden. The draft stays with the caller, which must not dispose
        ///     it until the returned task completes.
        /// </remarks>
        /// <returns>The selection, or null when the user cancelled.</returns>
        /// <exception cref="InvalidOperationException">Another view of the suite is open.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled; the view is closed.</exception>
        public Task<ReplaySelection> TrimAsync(ReplayDraft draft, CancellationToken cancellationToken = default)
        {
            return GetView().TrimAsync(draft, cancellationToken);
        }

        /// <summary>
        ///     Shows a list of the recordings in the library and of the footage left behind by abnormal terminations, and
        ///     returns the recordings chosen by the user. Chosen footage of the latter kind is recovered into the library
        ///     first.
        /// </summary>
        /// <remarks>The view is shown even while the overlay is hidden.</remarks>
        /// <param name="allowMultiple">Whether the user can choose more than one recording.</param>
        /// <param name="cancellationToken">Closes the view when cancelled.</param>
        /// <returns>The chosen recordings, newest first after any recovered ones. Empty when the user cancelled.</returns>
        /// <exception cref="InvalidOperationException">Another view of the suite is open.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled; the view is closed.</exception>
        public Task<IReadOnlyList<RecordingEntry>> PickRecordingsAsync(bool allowMultiple = true,
            CancellationToken cancellationToken = default)
        {
            return GetView().PickRecordingsAsync(allowMultiple, cancellationToken);
        }

        private SuiteView GetView()
        {
            if (_view == null) throw new InvalidOperationException("The InstantReplaySuite is not active.");
            return _view;
        }

        private void Awake()
        {
#if UNITY_EDITOR
            AssignDefaultAssets();
#endif
            if (Current != null && Current != this)
            {
                // A second recorder would create a second set of providers, which some providers do not allow.
                Debug.LogWarning("Another InstantReplaySuite is already active. This one is destroyed.", this);
                Destroy(gameObject);
                return;
            }

            Current = this;
            if (dontDestroyOnLoad && transform.parent == null) DontDestroyOnLoad(gameObject);

            if (themeStyleSheet == null)
                Debug.LogWarning("InstantReplaySuite has no theme style sheet. Controls may not render correctly.",
                    this);

            _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _panelSettings.name = "InstantReplaySuite PanelSettings";
            _panelSettings.hideFlags = HideFlags.DontSave;
            _panelSettings.themeStyleSheet = themeStyleSheet;
            _panelSettings.sortingOrder = sortingOrder;
            _panelSettings.scaleMode = PanelScaleMode.ConstantPhysicalSize;
            // The Device variant reports the simulated device in the Device Simulator.
            var referenceDpi = UnityEngine.Device.SystemInfo.deviceType == DeviceType.Handheld ? HandheldReferenceDpi : DesktopReferenceDpi;
            _panelSettings.referenceDpi = referenceDpi / Mathf.Clamp(scale, 0.1f, 10f);
            // Used when the platform does not report the screen density, in which case one unit is one pixel.
            _panelSettings.fallbackDpi = _panelSettings.referenceDpi;
#if !UNITY_6000_0_OR_NEWER
            _systemFontFallback = SystemFontFallback.Create();
            if (_systemFontFallback != null) _panelSettings.textSettings = _systemFontFallback.TextSettings;
#endif

            var document = gameObject.AddComponent<UIDocument>();
            document.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
            document.panelSettings = _panelSettings;

            // Without a settings asset, the recorder uses default settings, which persist the preferences.
            _preferences = settings == null || settings.PersistUserPreferences ? SuitePreferences.Load() : null;
            if (_preferences is { hasOverlayCorner: true }) overlayCorner = _preferences.overlayCorner;

            Strings.SetLanguage(language);
            _view = new SuiteView(document.rootVisualElement, styleSheet, Recorder, transform)
            {
                IsVisible = showOverlayOnLaunch, OverlayOpensPanel = overlayOpensPanel, OverlayCorner = overlayCorner
            };
            _view.OverlayCornerChanged += OnOverlayCornerChanged;
        }

        private void OnOverlayCornerChanged(OverlayCorner corner)
        {
            overlayCorner = corner;
            if (_preferences == null) return;
            _preferences.hasOverlayCorner = true;
            _preferences.overlayCorner = corner;
            _preferences.Save();
        }

        private void Start()
        {
            if (Current != this) return;
            Recorder.Activate();
        }

        private void OnDestroy()
        {
            if (Current != this) return;
            Current = null;

            _view?.Dispose();
            _view = null;
            _recorder?.Dispose();
            _recorder = null;
            if (_panelSettings != null) Destroy(_panelSettings);
#if !UNITY_6000_0_OR_NEWER
            _systemFontFallback?.Dispose();
            _systemFontFallback = null;
#endif
        }

#if UNITY_EDITOR
        private void Reset()
        {
            AssignDefaultAssets();
        }

        private void OnValidate()
        {
            AssignDefaultAssets();
        }

        private void AssignDefaultAssets()
        {
            if (themeStyleSheet == null)
                themeStyleSheet =
                    UnityEditor.AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                        PackagePath + "UI/InstantReplaySuiteTheme.tss");
            if (styleSheet == null)
                styleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>(
                    PackagePath + "UI/InstantReplaySuite.uss");
        }
#endif
    }

    /// <summary>
    ///     Choices about the interface made by the user that survive a relaunch. Choices about the recording are kept by
    ///     the recorder in a separate file.
    /// </summary>
    [Serializable]
    internal sealed class SuitePreferences
    {
        private const int CurrentVersion = 1;

        public int version = CurrentVersion;
        public bool hasOverlayCorner;
        public OverlayCorner overlayCorner;

        private static string FilePath =>
            Path.Combine(Application.persistentDataPath, "InstantReplay", "suite-ui-preferences.json");

        public static SuitePreferences Load()
        {
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return new SuitePreferences();
                var loaded = JsonUtility.FromJson<SuitePreferences>(File.ReadAllText(path));
                if (loaded is not { version: CurrentVersion }) return new SuitePreferences();
                if (!Enum.IsDefined(typeof(OverlayCorner), loaded.overlayCorner)) loaded.hasOverlayCorner = false;
                return loaded;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return new SuitePreferences();
            }
        }

        public void Save()
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(this));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}
