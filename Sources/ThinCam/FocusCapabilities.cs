namespace ThinCam;

/// <summary>
/// Describes the focus settings supported by a camera.
/// </summary>
/// <param name="Modes">The supported focus modes.</param>
/// <param name="ManualPosition">The supported manual focus position range, if available.</param>
public sealed record FocusCapabilities(IReadOnlySet<FocusMode> Modes,
                                       NumericRange<double>? ManualPosition)
{
    /// <summary>
    /// Gets whether any focus control is supported.
    /// </summary>
    public bool IsSupported => Modes.Count != 0 || ManualPosition is not null;
}
