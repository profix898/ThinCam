using System.Runtime.InteropServices;

namespace ThinCam.Interop;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFrame
{
    internal uint StructSize;
    internal byte* Data;
    internal nuint DataLength;
    internal int Width;
    internal int Height;
    internal int Stride;
    internal NativePixelFormat PixelFormat;
    internal int RotationDegrees;
    internal int Mirrored;
    internal long TimestampMicroseconds;
}
