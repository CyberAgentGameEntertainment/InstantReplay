// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

#if !EXCLUDE_INSTANTREPLAY

using System.Linq;
using UnityEditor;
using UnityEditor.Networking.PlayerConnection;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;

namespace UniEnc.Unity.Editor
{
    /// <summary>
    ///     Preferences page for the native (Rust) log level, and the Editor half of the protocol that
    ///     mirrors that level onto connected development players.
    /// </summary>
    internal static class NativeLoggingPreferences
    {
        private const string LevelKey = "UniEnc.NativeLogLevel";

        private const string BakeKey = "UniEnc.BakeNativeLogLevel";

        /// <summary>
        ///     Stored value meaning "leave the level the native library picked for itself".
        /// </summary>
        internal const int Unset = -1;

        /// <summary>
        ///     Indexed by <see cref="NativeLogLevel" /> plus one, with <see cref="Unset" /> first.
        /// </summary>
        private static readonly string[] LevelLabels =
        {
            "Default (build-dependent)", "Off", "Error", "Warning", "Info", "Debug", "Trace"
        };

        private static readonly GUIContent LevelLabel = new("Native Log Level",
            "Native log records below this level are discarded.");

        private static readonly GUIContent BakeLabel = new("Bake Into Player Builds",
            "Writes the level into every player you build from this Editor, so it is in effect from the "
            + "first frame instead of waiting for a player connection.");

        internal static int StoredLevel
        {
            get => EditorPrefs.GetInt(LevelKey, Unset);
            private set => EditorPrefs.SetInt(LevelKey, value);
        }

        /// <summary>
        ///     Off by default: a level chosen to debug the Editor should not silently follow a build out of
        ///     the door.
        /// </summary>
        internal static bool BakeIntoBuilds
        {
            get => EditorPrefs.GetBool(BakeKey, false);
            private set => EditorPrefs.SetBool(BakeKey, value);
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            Apply(StoredLevel);

            EditorConnection.instance.Initialize();
            EditorConnection.instance.Register(NativeLoggingPlayerConnection.RequestMessageId, OnPlayerRequest);
            EditorConnection.instance.RegisterConnection(OnPlayerConnected);
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorConnection.instance.Unregister(NativeLoggingPlayerConnection.RequestMessageId, OnPlayerRequest);
            EditorConnection.instance.UnregisterConnection(OnPlayerConnected);
        }

        /// <summary>
        ///     Applies the level to the Editor's own copy of the plugin.
        /// </summary>
        private static void Apply(int stored)
        {
            // Leaving the native library alone while nothing is configured keeps the plugin unloaded — and
            // so replaceable on disk — in a session that never touches the encoder.
            if (stored == Unset) return;

            NativeLogging.SetLevel((NativeLogLevel)stored);
        }

        /// <summary>
        ///     Pushes the level to every connected player, or to one of them when <paramref name="playerId" />
        ///     is given.
        /// </summary>
        private static void Send(int stored, int playerId = -1)
        {
            if (stored == Unset) return;

            var payload = new[] { (byte)stored };
            if (playerId < 0)
                EditorConnection.instance.Send(NativeLoggingPlayerConnection.LevelMessageId, payload);
            else
                EditorConnection.instance.Send(NativeLoggingPlayerConnection.LevelMessageId, payload, playerId);
        }

        /// <summary>
        ///     Covers a player that is up and listening before the Editor attaches to it. A player that
        ///     starts with the Editor already running normally connects before its own managed code runs, so
        ///     this push lands before there is a handler to receive it — <see cref="OnPlayerRequest" /> is
        ///     what serves that, much more common, case.
        /// </summary>
        private static void OnPlayerConnected(int playerId)
        {
            Send(StoredLevel, playerId);
        }

        /// <summary>
        ///     A player asks for the current level once it has registered its handler, which is the point
        ///     from which it can actually receive one.
        /// </summary>
        private static void OnPlayerRequest(MessageEventArgs args)
        {
            Send(StoredLevel, args.playerId);
        }

        [SettingsProvider]
        private static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Preferences/UniEnc", SettingsScope.User)
            {
                label = "UniEnc",
                guiHandler = OnGui,
                keywords = new[] { "unienc", "instant replay", "native", "log", "logging", "level", "verbosity" }
            };
        }

        private static void OnGui(string searchContext)
        {
            EditorGUILayout.Space();

            using (new EditorGUI.IndentLevelScope())
            {
                var stored = StoredLevel;

                EditorGUI.BeginChangeCheck();
                var index = EditorGUILayout.Popup(LevelLabel, stored == Unset ? 0 : stored + 1, LevelLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    stored = index == 0 ? Unset : index - 1;
                    StoredLevel = stored;
                    Apply(stored);
                    Send(stored);
                }

                using (new EditorGUI.DisabledScope(stored == Unset))
                {
                    EditorGUI.BeginChangeCheck();
                    var bake = EditorGUILayout.Toggle(BakeLabel, stored != Unset && BakeIntoBuilds);
                    if (EditorGUI.EndChangeCheck()) BakeIntoBuilds = bake;
                }

                EditorGUILayout.HelpBox(
                    "Applies to the Editor's own copy of the native plugin, and is pushed to every connected " +
                    "development player. \"Default\" leaves the level the native library picked for itself: " +
                    "Warn for a release build of the plugin, Debug for a debug build.\n\n" +
                    "A player connection can only deliver a level once the player is running, which is too " +
                    "late for the records a session emits while starting up. Bake the level in to catch " +
                    "those. Baking applies to release builds too, and is per-Editor: a machine that never " +
                    "set a level here — a build machine, say — bakes nothing.",
                    MessageType.None);

                DrawConnectedPlayers(stored);
            }
        }

        private static void DrawConnectedPlayers(int stored)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Connected Players", EditorStyles.boldLabel);

            var players = EditorConnection.instance.ConnectedPlayers.ToArray();

            using (new EditorGUI.IndentLevelScope())
            {
                if (players.Length == 0)
                    EditorGUILayout.LabelField("None", EditorStyles.miniLabel);
                else
                    foreach (var player in players)
                        EditorGUILayout.LabelField($"{player.name} (id {player.playerId})", EditorStyles.miniLabel);
            }

            using (new EditorGUI.DisabledScope(stored == Unset || players.Length == 0))
            {
                if (GUILayout.Button("Resend To Connected Players", GUILayout.ExpandWidth(false)))
                    Send(stored);
            }
        }
    }
}

#endif
