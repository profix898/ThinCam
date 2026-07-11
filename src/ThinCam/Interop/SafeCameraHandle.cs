using Microsoft.Win32.SafeHandles;

namespace ThinCam.Interop;

internal sealed class SafeCameraHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeCameraHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.CameraClose(handle);
        return true;
    }
}
