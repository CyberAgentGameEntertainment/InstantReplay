using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UniEnc.Example
{
    internal static class NativeLibraryResolver
    {
        [ModuleInitializer]
        public static void Initialize()
        {
            NativeLibrary.SetDllImportResolver(typeof(EncodingSystem).Assembly, (name, _, _) =>
            {
                if (!name.Contains("libunienc"))
                    return nint.Zero;

                string ext;
                string platform;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    platform = "win";
                    ext = ".dll";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    platform = "osx";
                    ext = ".dylib";
                }
                else
                {
                    return nint.Zero;
                }

                var arch = RuntimeInformation.OSArchitecture switch
                {
                    Architecture.Arm64 => "arm64",
                    Architecture.X64 => "x64",
                    Architecture.X86 => "x86",
                    _ => throw new NotSupportedException()
                };

                return NativeLibrary.Load(Path.Combine($"runtimes/{platform}-{arch}/native/{name}{ext}"));
            });
        }
    }
}
