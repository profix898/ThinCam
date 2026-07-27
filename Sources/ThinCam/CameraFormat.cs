namespace ThinCam;

public sealed record CameraFormat(
    int Width,
    int Height,
    int Stride,
    PixelFormat PixelFormat);
