using System.Runtime.InteropServices;

namespace ThinCam.Interop;

internal enum NativeStatus
{
    Ok = 0,
    InvalidArgument = 1,
    NotSupported = 2,
    PermissionDenied = 3,
    DeviceNotFound = 4,
    DeviceBusy = 5,
    FormatNotSupported = 6,
    NotRunning = 7,
    AlreadyRunning = 8,
    Platform = 9,
    Timeout = 10,
    Cancelled = 11
}

internal enum NativePermissionStatus
{
    Unknown = 0,
    NotDetermined = 1,
    Granted = 2,
    Denied = 3,
    Restricted = 4,
    HostActionRequired = 5
}

internal enum NativeCameraPosition
{
    Unspecified = 0,
    Front = 1,
    Back = 2,
    External = 3
}

internal enum NativePixelFormat
{
    Bgra32 = 1
}

internal enum NativeControlId
{
    ExposureMode = 1,
    ExposureCompensationEv = 2,
    ExposureDurationMicroseconds = 3,
    ExposureIso = 4,
    FocusMode = 10,
    FocusPosition = 11,
    ZoomFactor = 20,
    LightEnabled = 30,
    LightLevel = 31
}

internal enum NativeControlValueType
{
    Boolean = 1,
    Int64 = 2,
    Double = 3,
    Enum = 4
}

[Flags]
internal enum NativeControlFlags : uint
{
    None = 0,
    Readable = 1 << 0,
    Writable = 1 << 1
}

internal enum NativeExposureMode
{
    Auto = 1,
    Manual = 2,
    Locked = 3
}

internal enum NativeFocusMode
{
    Auto = 1,
    ContinuousAuto = 2,
    Manual = 3,
    Locked = 4
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDeviceInfo
{
    internal uint StructSize;
    internal byte* Id;
    internal byte* Name;
    internal NativeCameraPosition Position;
    internal int IsDefault;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOpenOptions
{
    internal uint StructSize;
    internal int Width;
    internal int Height;
    internal int FramesPerSecond;
    internal NativePixelFormat PixelFormat;
}

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

[StructLayout(LayoutKind.Explicit)]
internal struct NativeControlData
{
    [FieldOffset(0)] internal int BooleanValue;
    [FieldOffset(0)] internal int EnumValue;
    [FieldOffset(0)] internal long IntegerValue;
    [FieldOffset(0)] internal double DoubleValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeControlValue
{
    internal uint StructSize;
    internal NativeControlId Id;
    internal NativeControlValueType ValueType;
    internal uint Reserved;
    internal NativeControlData Value;

    internal static NativeControlValue Boolean(NativeControlId id, bool value) => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeControlValue>(),
        Id = id,
        ValueType = NativeControlValueType.Boolean,
        Value = new NativeControlData { BooleanValue = value ? 1 : 0 }
    };

    internal static NativeControlValue Enum(NativeControlId id, int value) => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeControlValue>(),
        Id = id,
        ValueType = NativeControlValueType.Enum,
        Value = new NativeControlData { EnumValue = value }
    };

    internal static NativeControlValue Int64(NativeControlId id, long value) => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeControlValue>(),
        Id = id,
        ValueType = NativeControlValueType.Int64,
        Value = new NativeControlData { IntegerValue = value }
    };

    internal static NativeControlValue Double(NativeControlId id, double value) => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeControlValue>(),
        Id = id,
        ValueType = NativeControlValueType.Double,
        Value = new NativeControlData { DoubleValue = value }
    };
}
