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
    ///     Text settings for the panel of the suite that fall back to a font supplied by the application and, in Unity
    ///     2022.3, to the fonts installed on the device, so that Japanese text is shown even though the font of the default
    ///     theme has no Japanese characters.
    /// </summary>
    /// <remarks>
    ///     Unity 6 falls back to the fonts of the operating system by itself, except on Web, where no system font is
    ///     available to the player. Unity 2022.3 does not fall back by itself.
    /// </remarks>
    internal sealed class FallbackFonts : IDisposable
    {
#if !UNITY_6000_0_OR_NEWER
        // Family and style names as reported by FontEngine.GetSystemFontNames, in order of preference. Missing fonts are
        // skipped.
        private static readonly (string Family, string Style)[] SystemFontCandidates =
        {
            ("Hiragino Sans", "W3"), // iOS, macOS
            ("Hiragino Kaku Gothic ProN", "W3"), // macOS
            ("Noto Sans CJK JP", "Regular"), // Android
            ("Noto Sans JP", "Regular"), // Android
            ("Yu Gothic UI", "Regular"), // Windows
            ("Meiryo UI", "Regular"), // Windows
            ("MS Gothic", "Regular") // Windows
        };
#endif

        private readonly List<FontAsset> _fontAssets;

        private FallbackFonts(List<FontAsset> fontAssets)
        {
            _fontAssets = fontAssets;
            TextSettings = ScriptableObject.CreateInstance<PanelTextSettings>();
            TextSettings.name = "InstantReplaySuite PanelTextSettings";
            TextSettings.hideFlags = HideFlags.DontSave;
            TextSettings.fallbackFontAssets = new List<FontAsset>(fontAssets);
        }

        public PanelTextSettings TextSettings { get; }

        public void Dispose()
        {
            foreach (var fontAsset in _fontAssets)
            {
                if (fontAsset == null) continue;
                if (fontAsset.atlasTextures != null)
                    foreach (var texture in fontAsset.atlasTextures)
                        if (texture != null)
                            Object.Destroy(texture);
                if (fontAsset.material != null) Object.Destroy(fontAsset.material);
                Object.Destroy(fontAsset);
            }

            if (TextSettings != null) Object.Destroy(TextSettings);
        }

        /// <summary>
        ///     Creates the text settings with <paramref name="font" /> as the first fallback.
        /// </summary>
        /// <param name="font">Font supplied by the application, or null.</param>
        /// <returns>Null when there is no font to fall back to, in which case the default text settings are kept.</returns>
        public static FallbackFonts Create(Font font)
        {
            var fontAssets = new List<FontAsset>();

            if (font != null)
            {
                var fontAsset = FontAsset.CreateFontAsset(font);
                if (fontAsset != null)
                {
                    fontAsset.hideFlags = HideFlags.DontSave;
                    fontAssets.Add(fontAsset);
                }
                else
                {
                    ILogger.LogWarningCore(
                        $"InstantReplaySuite could not use the font '{font.name}'. Make sure that its font data is included.");
                }
            }

#if !UNITY_6000_0_OR_NEWER
            var systemFontAsset = CreateSystemFontAsset();
            if (systemFontAsset != null) fontAssets.Add(systemFontAsset);
#endif

            return fontAssets.Count > 0 ? new FallbackFonts(fontAssets) : null;
        }

#if !UNITY_6000_0_OR_NEWER
        // Returns null when none of the fonts is installed.
        private static FontAsset CreateSystemFontAsset()
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
            foreach (var (family, style) in SystemFontCandidates)
            {
                if (!names.Contains($"{family} - {style}")) continue;
                var fontAsset = FontAsset.CreateFontAsset(family, style);
                if (fontAsset == null) continue;
                fontAsset.hideFlags = HideFlags.DontSave;
                return fontAsset;
            }

            return null;
        }
#endif
    }
}
