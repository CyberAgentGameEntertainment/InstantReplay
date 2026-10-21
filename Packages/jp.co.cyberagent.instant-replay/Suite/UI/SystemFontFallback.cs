// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Text settings for the panel of the suite that fall back to the fonts installed on the device, so that Japanese
    ///     text is shown without bundling a font.
    /// </summary>
    /// <remarks>
    ///     Unity 6 falls back to the fonts of the operating system by itself. Unity 2022.3 does not, and the font of the
    ///     default theme has no Japanese characters.
    /// </remarks>
    internal sealed class SystemFontFallback : IDisposable
    {
        // Family and style names as reported by FontEngine.GetSystemFontNames, in order of preference. Missing fonts are
        // skipped.
        private static readonly (string Family, string Style)[] Candidates =
        {
            ("Hiragino Sans", "W3"), // iOS, macOS
            ("Hiragino Kaku Gothic ProN", "W3"), // macOS
            ("Noto Sans CJK JP", "Regular"), // Android
            ("Noto Sans JP", "Regular"), // Android
            ("Yu Gothic UI", "Regular"), // Windows
            ("Meiryo UI", "Regular"), // Windows
            ("MS Gothic", "Regular") // Windows
        };

        private readonly FontAsset _fontAsset;

        private SystemFontFallback(FontAsset fontAsset)
        {
            _fontAsset = fontAsset;
            TextSettings = ScriptableObject.CreateInstance<PanelTextSettings>();
            TextSettings.name = "InstantReplaySuite PanelTextSettings";
            TextSettings.hideFlags = HideFlags.DontSave;
            TextSettings.fallbackFontAssets = new List<FontAsset> { fontAsset };
        }

        public PanelTextSettings TextSettings { get; }

        public void Dispose()
        {
            if (_fontAsset != null)
            {
                if (_fontAsset.atlasTextures != null)
                    foreach (var texture in _fontAsset.atlasTextures)
                        if (texture != null)
                            Object.Destroy(texture);
                if (_fontAsset.material != null) Object.Destroy(_fontAsset.material);
                Object.Destroy(_fontAsset);
            }

            if (TextSettings != null) Object.Destroy(TextSettings);
        }

        /// <summary>Returns null when none of the fonts is installed.</summary>
        public static SystemFontFallback Create()
        {
            string[] installed;
            try
            {
                installed = FontEngine.GetSystemFontNames();
            }
            catch (Exception ex)
            {
                ILogger.LogWarningCore($"InstantReplaySuite could not list the system fonts: {ex.Message}");
                return null;
            }

            if (installed == null) return null;
            var names = new HashSet<string>(installed);

            // One font covers the Japanese characters, so the first one found is used.
            foreach (var (family, style) in Candidates)
            {
                if (!names.Contains($"{family} - {style}")) continue;
                var fontAsset = FontAsset.CreateFontAsset(family, style);
                if (fontAsset == null) continue;
                fontAsset.hideFlags = HideFlags.DontSave;
                return new SystemFontFallback(fontAsset);
            }

            return null;
        }
    }
}
