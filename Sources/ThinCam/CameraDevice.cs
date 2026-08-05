namespace ThinCam;

/// <summary>
/// Describes an available camera device.
/// </summary>
/// <param name="Id">The platform-specific device identifier.</param>
/// <param name="Name">The display name of the device.</param>
/// <param name="Position">The physical position of the device.</param>
/// <param name="IsDefault">Whether the device is the platform default.</param>
public sealed record CameraDevice(string Id,
                                  string Name,
                                  CameraPosition Position,
                                  bool IsDefault);
