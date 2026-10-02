// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;

namespace UniEnc.Unity
{
    /// <summary>
    ///     Player half of the protocol that mirrors the Editor's native log level onto a connected player.
    ///     Only development players take part: the player connection this rides on does not exist in a
    ///     release build.
    /// </summary>
    internal static class NativeLoggingPlayerConnection
    {
        /// <summary>
        ///     Editor to player: a single byte holding the <see cref="NativeLogLevel" /> to apply.
        /// </summary>
        internal static readonly Guid LevelMessageId = new("eaf1201c-a963-401e-b12c-af28428a5805");

        /// <summary>
        ///     Player to editor: a single protocol-version byte, asking for the level the Editor currently
        ///     has configured.
        /// </summary>
        internal static readonly Guid RequestMessageId = new("1351559c-9b22-4f19-bd75-7f06f55e0ce7");

        /// <summary>
        ///     Carried by the request so a future Editor can tell an old player apart from a new one.
        /// </summary>
        internal const byte ProtocolVersion = 1;

#if DEVELOPMENT_BUILD
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void Register()
        {
            var connection = PlayerConnection.instance;
            connection.Register(LevelMessageId, OnLevelReceived);

            // The Editor pushes the level as each player connects, but the connection is normally up
            // before managed code runs at all, so that push lands before the handler above exists. Asking
            // for the level from here is what actually covers the common case; RegisterConnection only
            // reports connections made from this point on, and covers an Editor that attaches later.
            if (connection.isConnected) RequestLevel();
            connection.RegisterConnection(_ => RequestLevel());
        }

        private static void RequestLevel()
        {
            PlayerConnection.instance.Send(RequestMessageId, new[] { ProtocolVersion });
        }

        private static void OnLevelReceived(MessageEventArgs args)
        {
            if (args.data is not { Length: > 0 }) return;

            var raw = args.data[0];
            if (raw > (byte)NativeLogLevel.Trace)
            {
                Debug.LogWarning($"[unienc] Ignoring unknown native log level {raw} sent by the Editor.");
                return;
            }

            var level = (NativeLogLevel)raw;
            NativeLogging.SetLevel(level);
            Debug.Log($"[unienc] Native log level set to {level} by the Editor.");
        }
#endif
    }
}
