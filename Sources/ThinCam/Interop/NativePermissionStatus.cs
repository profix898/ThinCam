namespace ThinCam.Interop;

internal enum NativePermissionStatus
{
    Unknown = 0,
    NotDetermined = 1,
    Granted = 2,
    Denied = 3,
    Restricted = 4,
    HostActionRequired = 5
}
