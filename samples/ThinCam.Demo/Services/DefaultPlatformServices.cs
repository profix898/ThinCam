using System.Runtime.InteropServices;

namespace ThinCam.Demo.Services;

public sealed class DefaultPlatformServices : IPlatformServices
{
    public string PlatformDescription =>
        $"{RuntimeInformation.OSDescription.Trim()} / {RuntimeInformation.ProcessArchitecture} / .NET {Environment.Version}";

    public ValueTask<CameraPermissionStatus> RequestCameraPermissionAsync(
        CancellationToken cancellationToken = default) =>
        CameraPermissions.RequestAsync(cancellationToken);
}
