using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ThinCam.Interop;

internal sealed class SafeCameraHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private GCHandle _state;

    internal SafeCameraHandle(nint handle, GCHandle state)
        : base(true)
    {
        SetHandle(handle);
        _state = state;
    }

    protected override bool ReleaseHandle()
    {
        // tc_camera_close stops capture and drains callbacks, so the callback state can only be
        // released afterwards. Owning both here keeps them atomic even on the finalizer path.
        NativeMethods.CameraClose(handle);
        if (_state.IsAllocated)
            _state.Free();
        return true;
    }
}
