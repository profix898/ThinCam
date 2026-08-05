using System.Runtime.InteropServices;

namespace ThinCam.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeControlValue
{
    internal uint StructSize;
    internal NativeControlId Id;
    internal NativeControlValueType ValueType;
    internal uint Reserved;
    internal NativeControlData Value;

    internal static NativeControlValue Boolean(NativeControlId id, bool value)
        => new()
        {
            StructSize = (uint) Marshal.SizeOf<NativeControlValue>(), Id = id, ValueType = NativeControlValueType.Boolean,
            Value = new NativeControlData { BooleanValue = value ? 1 : 0 }
        };

    internal static NativeControlValue Enum(NativeControlId id, int value)
        => new()
        {
            StructSize = (uint) Marshal.SizeOf<NativeControlValue>(), Id = id, ValueType = NativeControlValueType.Enum, Value = new NativeControlData { EnumValue = value }
        };

    internal static NativeControlValue Int64(NativeControlId id, long value)
        => new()
        {
            StructSize = (uint) Marshal.SizeOf<NativeControlValue>(), Id = id, ValueType = NativeControlValueType.Int64, Value = new NativeControlData { IntegerValue = value }
        };

    internal static NativeControlValue Double(NativeControlId id, double value)
        => new()
        {
            StructSize = (uint) Marshal.SizeOf<NativeControlValue>(), Id = id, ValueType = NativeControlValueType.Double, Value = new NativeControlData { DoubleValue = value }
        };
}
