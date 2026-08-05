using ThinCam;

namespace ThinCamDemo.Services;

/// <summary>Provides host-platform services used by the demo.</summary>
public interface IPlatformServices
{
    /// <summary>Gets a description of the current platform.</summary>
    string PlatformDescription { get; }

    /// <summary>Requests permission to access camera devices.</summary>
    /// <param name="cancellationToken">A token that can cancel the request.</param>
    /// <returns>The resulting camera permission status.</returns>
    ValueTask<CameraPermissionStatus> RequestCameraPermissionAsync(CancellationToken cancellationToken = default);
}
