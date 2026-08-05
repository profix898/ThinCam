namespace ThinCam;

/// <summary>
/// Describes the exposure settings supported by a camera.
/// </summary>
/// <param name="Modes">The supported exposure modes.</param>
/// <param name="CompensationEv">The supported exposure compensation range in EV, if available.</param>
/// <param name="Duration">The supported exposure duration range, if available.</param>
/// <param name="Iso">The supported ISO sensitivity range, if available.</param>
public sealed record ExposureCapabilities(IReadOnlySet<ExposureMode> Modes,
                                          NumericRange<double>? CompensationEv,
                                          NumericRange<TimeSpan>? Duration,
                                          NumericRange<double>? Iso)
{
    /// <summary>
    /// Gets whether any exposure control is supported.
    /// </summary>
    public bool IsSupported => Modes.Count != 0 || CompensationEv is not null || Duration is not null || Iso is not null;
}
