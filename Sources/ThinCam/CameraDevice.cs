namespace ThinCam;

public sealed record CameraDevice(
    string Id,
    string Name,
    CameraPosition Position,
    bool IsDefault);
