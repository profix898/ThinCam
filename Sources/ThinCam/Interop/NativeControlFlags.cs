namespace ThinCam.Interop;

[Flags]
internal enum NativeControlFlags : uint
{
    None = 0,
    Readable = 1 << 0,
    Writable = 1 << 1
}
