namespace ThinCam.Demo.Services;

public interface IPlatformServices
{
    string PlatformDescription { get; }

    ValueTask<CameraPermissionStatus> RequestCameraPermissionAsync(
        CancellationToken cancellationToken = default);
}
