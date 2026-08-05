using System.Runtime.InteropServices;

namespace ThinCam.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOpenOptions
{
    internal uint StructSize;
    internal int Width;
    internal int Height;
    internal int FramesPerSecond;
    internal NativePixelFormat PixelFormat;
}
