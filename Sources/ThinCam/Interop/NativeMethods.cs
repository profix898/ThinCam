using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ThinCam.Interop;

internal static partial class NativeMethods
{
#if IOS
    private const string LibraryName = "__Internal";
#else
    private const string LibraryName = "thincam";
#endif

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_get_abi_version")]
    internal static partial uint GetAbiVersion();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_status_message")]
    internal static partial nint GetStatusMessage(NativeStatus status);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_get_permission_status")]
    internal static partial NativeStatus GetPermissionStatus(out NativePermissionStatus status);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_request_permission")]
    internal static partial NativeStatus RequestPermission(nint callback, nint userData);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_enumerate_devices")]
    internal static partial NativeStatus EnumerateDevices(nint callback, nint userData);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_camera_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial NativeStatus CameraOpen(string deviceId,
                                                    in NativeOpenOptions options,
                                                    nint frameCallback,
                                                    nint errorCallback,
                                                    nint userData,
                                                    out nint camera);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_camera_start")]
    internal static partial NativeStatus CameraStart(nint camera);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_camera_stop")]
    internal static partial NativeStatus CameraStop(nint camera);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_camera_close")]
    internal static partial void CameraClose(nint camera);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_camera_get_control_info")]
    internal static partial NativeStatus CameraGetControlInfo(nint camera,
                                                              NativeControlId id,
                                                              out NativeControlInfo info);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_camera_get_control")]
    internal static partial NativeStatus CameraGetControl(nint camera,
                                                          NativeControlId id,
                                                          out NativeControlValue value);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [LibraryImport(LibraryName, EntryPoint = "tc_camera_set_control")]
    internal static partial NativeStatus CameraSetControl(nint camera,
                                                          in NativeControlValue value);
}
