namespace ThinCam;

/// <summary>
/// Describes the current state of a camera light.
/// </summary>
/// <param name="IsEnabled">Whether the light is enabled, if known.</param>
/// <param name="Level">The current light level, if known.</param>
public sealed record CameraLightState(bool? IsEnabled,
                                       double? Level);
