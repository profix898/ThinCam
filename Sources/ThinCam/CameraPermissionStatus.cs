namespace ThinCam;

/// <summary>
/// Describes the application's camera permission state.
/// </summary>
public enum CameraPermissionStatus
{
    /// <summary>
    /// The permission state is unknown.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Camera permission has not been requested.
    /// </summary>
    NotDetermined = 1,

    /// <summary>
    /// Camera permission is granted.
    /// </summary>
    Granted = 2,

    /// <summary>
    /// Camera permission is denied.
    /// </summary>
    Denied = 3,

    /// <summary>
    /// Camera access is restricted by the system.
    /// </summary>
    Restricted = 4,

    /// <summary>
    /// The host application must complete a platform-specific action.
    /// </summary>
    HostActionRequired = 5
}
