namespace ThinCam;

/// <summary>
/// Describes the controls supported by a camera.
/// </summary>
/// <param name="Exposure">The exposure capabilities.</param>
/// <param name="Focus">The focus capabilities.</param>
/// <param name="Zoom">The zoom capabilities.</param>
/// <param name="Light">The camera light capabilities.</param>
public sealed record CameraCapabilities(ExposureCapabilities Exposure,
                                        FocusCapabilities Focus,
                                        ZoomCapabilities Zoom,
                                        CameraLightCapabilities Light);
