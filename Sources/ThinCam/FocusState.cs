namespace ThinCam;

/// <summary>
/// Describes the current focus state of a camera.
/// </summary>
/// <param name="Mode">The current focus mode, if known.</param>
/// <param name="Position">The current focus position, if known.</param>
public sealed record FocusState(FocusMode? Mode,
                                double? Position);
