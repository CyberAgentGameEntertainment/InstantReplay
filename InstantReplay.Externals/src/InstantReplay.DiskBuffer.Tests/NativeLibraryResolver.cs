// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UniEnc;

namespace InstantReplay.DiskBufferTests
{
    /// <summary>
    ///     Resolves the native library from the runtime identifier directories the UniEnc package lays out, so that the
    ///     end-to-end check can drive the real encoder and muxer.
    /// </summary>
    internal static class NativeLibraryResolver
    {
        [ModuleInitializer]
        public static void Initialize()
        {
            NativeLibrary.SetDllImportResolver(typeof(EncodingSystem).Assembly, (name, _, _) =>
            {
                if (!name.Contains("libunienc_c")) return IntPtr.Zero;

                string extension;
                string platform;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    platform = "win";
                    extension = ".dll";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    platform = "osx";
                    extension = ".dylib";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    platform = "linux";
                    extension = ".so";
                }
                else
                {
                    return IntPtr.Zero;
                }

                var architecture = RuntimeInformation.OSArchitecture switch
                {
                    Architecture.Arm64 => "arm64",
                    Architecture.X64 => "x64",
                    _ => null
                };

                if (architecture == null) return IntPtr.Zero;

                var path = Path.Combine(AppContext.BaseDirectory, "runtimes", $"{platform}-{architecture}", "native",
                    name + extension);

                return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
            });
        }
    }
}
