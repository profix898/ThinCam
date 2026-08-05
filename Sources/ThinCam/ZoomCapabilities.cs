namespace ThinCam;

/// <summary>
/// Describes the zoom settings supported by a camera.
/// </summary>
/// <param name="Factor">The supported zoom factor range, if available.</param>
public sealed record ZoomCapabilities(NumericRange<double>? Factor)
{
    /// <summary>
    /// Gets whether zoom control is supported.
    /// </summary>
    public bool IsSupported => Factor is not null;
}
