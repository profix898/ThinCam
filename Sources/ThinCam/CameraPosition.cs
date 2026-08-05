namespace ThinCam;

/// <summary>
/// Identifies the physical position of a camera.
/// </summary>
public enum CameraPosition
{
    /// <summary>
    /// The camera position is unspecified.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The camera faces the user.
    /// </summary>
    Front = 1,

    /// <summary>
    /// The camera faces away from the user.
    /// </summary>
    Back = 2,

    /// <summary>
    /// The camera is external to the device.
    /// </summary>
    External = 3
}
