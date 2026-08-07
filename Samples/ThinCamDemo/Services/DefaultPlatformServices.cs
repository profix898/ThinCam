using System.Runtime.InteropServices;
using ThinCam;

namespace ThinCamDemo.Services;

/// <summary>Provides platform services for desktop hosts.</summary>
public sealed class DefaultPlatformServices : IPlatformServices
{
    #region IPlatformServices

    /// <inheritdoc />
    public string PlatformDescription => $"{RuntimeInformation.OSDescription.Trim()} / {RuntimeInformation.ProcessArchitecture} / .NET {Environment.Version}";

    /// <inheritdoc />
    public ValueTask<CameraPermissionStatus> RequestCameraPermissionAsync(CancellationToken cancellationToken = default) => CameraPermissions.RequestAsync(cancellationToken);

    #endregion
}
