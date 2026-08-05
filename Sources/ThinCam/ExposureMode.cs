namespace ThinCam;

/// <summary>
/// Identifies a camera exposure mode.
/// </summary>
public enum ExposureMode
{
    /// <summary>
    /// Exposure is adjusted automatically.
    /// </summary>
    Auto = 1,

    /// <summary>
    /// Exposure settings are controlled manually.
    /// </summary>
    Manual = 2,

    /// <summary>
    /// Exposure is locked at its current settings.
    /// </summary>
    Locked = 3
}
