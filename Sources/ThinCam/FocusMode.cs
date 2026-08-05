namespace ThinCam;

/// <summary>
/// Identifies a camera focus mode.
/// </summary>
public enum FocusMode
{
    /// <summary>
    /// Focus is adjusted automatically once.
    /// </summary>
    Auto = 1,

    /// <summary>
    /// Focus is adjusted automatically and continuously.
    /// </summary>
    ContinuousAuto = 2,

    /// <summary>
    /// Focus position is controlled manually.
    /// </summary>
    Manual = 3,

    /// <summary>
    /// Focus is locked at its current position.
    /// </summary>
    Locked = 4
}
