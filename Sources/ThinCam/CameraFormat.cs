namespace ThinCam;

/// <summary>
/// Describes the layout and pixel format of a camera frame.
/// </summary>
/// <param name="Width">The frame width in pixels.</param>
/// <param name="Height">The frame height in pixels.</param>
/// <param name="Stride">The number of bytes per row.</param>
/// <param name="PixelFormat">The frame pixel format.</param>
public sealed record CameraFormat(int Width,
                                  int Height,
                                  int Stride,
                                  PixelFormat PixelFormat);
