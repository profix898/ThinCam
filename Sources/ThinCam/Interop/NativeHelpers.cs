using System.Reflection;
using System.Runtime.InteropServices;

namespace ThinCam.Interop;

internal static unsafe class NativeHelpers
{
    internal const uint AbiVersion = 2;

    private static int _resolverRegistered;

    static NativeHelpers()
    {
        RegisterResolver();
    }

    internal static void EnsureResolver() => RegisterResolver();

    private static void RegisterResolver()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) != 0)
            return;

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeHelpers).Assembly, ResolveLibrary);
        }
        catch
        {
            // SetDllImportResolver can only be called once per assembly.
        }
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is "thincam")
        {
            // On Windows the managed assembly is ThinCam.dll and the native library is thincam.dll.
            // On a case-insensitive filesystem they collide, so search the runtimes/<RID>/native
            // subdirectory first, where the NuGet/MSBuild staging places the native binary.
            if (OperatingSystem.IsWindows())
            {
                string[] candidates =
                [
                    Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "thincam.dll"),
                    Path.Combine(AppContext.BaseDirectory, "runtimes", "win-arm64", "native", "thincam.dll")
                ];

                foreach (var path in candidates)
                {
                    if (File.Exists(path))
                        return NativeLibrary.Load(path);
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libthincam.so");
                if (File.Exists(path))
                    return NativeLibrary.Load(path);
            }
            else if (OperatingSystem.IsMacOS())
            {
                string[] candidates =
                [
                    Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-arm64", "native", "libthincam.dylib"),
                    Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-x64", "native", "libthincam.dylib")
                ];

                foreach (var path in candidates)
                {
                    if (File.Exists(path))
                        return NativeLibrary.Load(path);
                }
            }
        }

        return IntPtr.Zero;
    }

    internal static void EnsureAbi()
    {
        EnsureResolver();
        try
        {
            var actual = NativeMethods.GetAbiVersion();
            if (actual != AbiVersion)
            {
                throw new CameraException(CameraErrorCode.NotSupported,
                                          $"ThinCam managed ABI {AbiVersion} cannot use native ABI {actual}.");
            }
        }
        catch (DllNotFoundException exception)
        {
            throw new CameraException(CameraErrorCode.Platform,
                                      "The ThinCam native library was not found for the current runtime identifier.",
                                      exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new CameraException(CameraErrorCode.NotSupported,
                                      "The installed ThinCam native library does not expose the expected ABI.",
                                      exception);
        }
    }

    internal static string Utf8(byte* value) => value is null ? String.Empty : Marshal.PtrToStringUTF8((nint) value) ?? String.Empty;

    internal static string StatusMessage(NativeStatus status)
    {
        var pointer = NativeMethods.GetStatusMessage(status);
        return pointer == 0
            ? status.ToString()
            : Marshal.PtrToStringUTF8(pointer) ?? status.ToString();
    }

    internal static CameraException Exception(NativeStatus status, string? detail = null)
    {
        var message = String.IsNullOrWhiteSpace(detail)
            ? StatusMessage(status)
            : detail;
        return new CameraException((CameraErrorCode) (int) status, message);
    }
}
