using System.Runtime.InteropServices;

namespace ThinCam.Interop;

internal static unsafe class NativeHelpers
{
    internal const uint AbiVersion = 2;

    internal static void EnsureAbi()
    {
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

    internal static string Utf8(byte* value)
    {
        return value is null ? String.Empty : Marshal.PtrToStringUTF8((nint) value) ?? String.Empty;
    }

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
