namespace ThinCam;

/// <summary>
/// Identifies a camera operation error.
/// </summary>
public enum CameraErrorCode
{
    /// <summary>
    /// The error is unknown.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// An argument is invalid.
    /// </summary>
    InvalidArgument = 1,

    /// <summary>
    /// The requested operation is not supported.
    /// </summary>
    NotSupported = 2,

    /// <summary>
    /// Camera access was denied.
    /// </summary>
    PermissionDenied = 3,

    /// <summary>
    /// The requested device was not found.
    /// </summary>
    DeviceNotFound = 4,

    /// <summary>
    /// The device is in use.
    /// </summary>
    DeviceBusy = 5,

    /// <summary>
    /// The requested camera format is not supported.
    /// </summary>
    FormatNotSupported = 6,

    /// <summary>
    /// The camera is not running.
    /// </summary>
    NotRunning = 7,

    /// <summary>
    /// The camera is already running.
    /// </summary>
    AlreadyRunning = 8,

    /// <summary>
    /// The platform camera API failed.
    /// </summary>
    Platform = 9,

    /// <summary>
    /// The operation timed out.
    /// </summary>
    Timeout = 10,

    /// <summary>
    /// The operation was cancelled.
    /// </summary>
    Cancelled = 11
}
