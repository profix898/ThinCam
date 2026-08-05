namespace ThinCam;

/// <summary>
/// Describes the current exposure state of a camera.
/// </summary>
/// <param name="Mode">The current exposure mode, if known.</param>
/// <param name="CompensationEv">The current exposure compensation in EV, if known.</param>
/// <param name="Duration">The current exposure duration, if known.</param>
/// <param name="Iso">The current ISO sensitivity, if known.</param>
public sealed record ExposureState(ExposureMode? Mode,
                                   double? CompensationEv,
                                   TimeSpan? Duration,
                                   double? Iso);
