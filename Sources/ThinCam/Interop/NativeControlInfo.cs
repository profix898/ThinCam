using System.Runtime.InteropServices;

namespace ThinCam.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeControlInfo
{
    internal uint StructSize;
    internal NativeControlId Id;
    internal NativeControlValueType ValueType;
    internal NativeControlFlags Flags;
    internal double Minimum;
    internal double Maximum;
    internal double Step;
    internal double DefaultValue;
    internal ulong SupportedEnumValues;
}
