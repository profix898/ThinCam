using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ThinCam.Interop;

namespace ThinCam;

public static class CameraDevices
{
    public static IReadOnlyList<CameraDevice> Enumerate()
    {
        NativeHelpers.EnsureAbi();
        var state = new DeviceEnumerationState();
        GCHandle handle = GCHandle.Alloc(state);

        try
        {
            unsafe
            {
                NativeStatus result = NativeMethods.EnumerateDevices(
                    (nint)(delegate* unmanaged[Cdecl]<NativeDeviceInfo*, nint, void>)&OnDevice,
                    GCHandle.ToIntPtr(handle));

                if (result != NativeStatus.Ok) throw NativeHelpers.Exception(result);
                if (state.Error is not null)
                {
                    throw new CameraException(
                        CameraErrorCode.Platform,
                        "Failed while processing an enumerated camera device.",
                        state.Error);
                }
            }
        }
        finally
        {
            handle.Free();
        }

        return state.Devices;
    }

    public static CameraDevice? Default
    {
        get
        {
            IReadOnlyList<CameraDevice> devices = Enumerate();
            return devices.FirstOrDefault(static device => device.IsDefault)
                ?? (devices.Count == 0 ? null : devices[0]);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDevice(NativeDeviceInfo* native, nint userData)
    {
        DeviceEnumerationState? state = null;
        try
        {
            if (native is null || native->StructSize < (uint)sizeof(NativeDeviceInfo)) return;

            GCHandle handle = GCHandle.FromIntPtr(userData);
            state = handle.Target as DeviceEnumerationState;
            if (state is null || state.Error is not null) return;

            string id = NativeHelpers.Utf8(native->Id);
            if (string.IsNullOrWhiteSpace(id)) return;

            string name = NativeHelpers.Utf8(native->Name);
            state.Devices.Add(new CameraDevice(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name,
                (CameraPosition)(int)native->Position,
                native->IsDefault != 0));
        }
        catch (Exception exception)
        {
            if (state is not null) state.Error = exception;
        }
    }

    private sealed class DeviceEnumerationState
    {
        internal List<CameraDevice> Devices { get; } = [];
        internal Exception? Error { get; set; }
    }
}
