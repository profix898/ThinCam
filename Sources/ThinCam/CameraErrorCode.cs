namespace ThinCam;

public enum CameraErrorCode
{
    Unknown = 0,
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
