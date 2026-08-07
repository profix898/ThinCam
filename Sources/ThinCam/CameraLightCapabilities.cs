namespace ThinCam;

/// <summary>
/// Describes the capabilities of a camera light.
/// </summary>
/// <param name="IsAvailable">Whether a camera light is available.</param>
/// <param name="SupportsVariableLevel">Whether the light level can be adjusted.</param>
/// <param name="Level">The supported light level range, if available.</param>
public sealed record CameraLightCapabilities(bool IsAvailable,
                                             bool SupportsVariableLevel,
                                             NumericRange<double>? Level);
