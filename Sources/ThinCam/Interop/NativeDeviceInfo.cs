using System.Runtime.InteropServices;

namespace ThinCam.Interop;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDeviceInfo
{
    internal uint StructSize;
    internal byte* Id;
    internal byte* Name;
    internal NativeCameraPosition Position;
    internal int IsDefault;
}
