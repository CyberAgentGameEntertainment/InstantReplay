// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using UnityEngine;

namespace InstantReplay.Suite.UI
{
    /// <summary>
    ///     Decodes thumbnail images. Requires the Image Conversion module; without it no thumbnail is shown.
    /// </summary>
    internal static class Thumbnails
    {
        /// <summary>Decodes a PNG image, or returns null when it cannot be decoded. The caller destroys the texture.</summary>
        public static Texture2D Decode(byte[] png)
        {
#if INSTANTREPLAY_IMAGECONVERSION
            if (png == null || png.Length == 0) return null;
            var texture = new Texture2D(2, 2) { name = "InstantReplaySuite Thumbnail" };
            if (texture.LoadImage(png, true)) return texture;
            UnityEngine.Object.Destroy(texture);
#endif
            return null;
        }

        /// <summary>Loads an image file, or returns null when it cannot be read. The caller destroys the texture.</summary>
        public static Texture2D Load(string path)
        {
#if INSTANTREPLAY_IMAGECONVERSION
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                return Decode(File.ReadAllBytes(path));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
#endif
            return null;
        }
    }
}
