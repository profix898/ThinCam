using System.Runtime.InteropServices;

namespace ThinCam.Interop;

[StructLayout(LayoutKind.Explicit)]
internal struct NativeControlData
{
    [FieldOffset(0)]
    internal int BooleanValue;

    [FieldOffset(0)]
    internal int EnumValue;

    [FieldOffset(0)]
    internal long IntegerValue;

    [FieldOffset(0)]
    internal double DoubleValue;
}
