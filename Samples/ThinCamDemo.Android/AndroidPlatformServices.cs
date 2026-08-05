using System.Runtime.InteropServices;
using Android.OS;
using ThinCam;
using ThinCamDemo.Services;
using Environment = System.Environment;

namespace ThinCamDemo.Android;

internal sealed class AndroidPlatformServices(Activity activity) : IPlatformServices
{
    private readonly Activity _activity =
        activity ?? throw new ArgumentNullException(nameof(activity));

    /// <inheritdoc />
    public string PlatformDescription
        => $"Android {(Build.VERSION.Release)} / " +
           $"{RuntimeInformation.ProcessArchitecture} / .NET {Environment.Version}";

    /// <inheritdoc />
    public ValueTask<CameraPermissionStatus> RequestCameraPermissionAsync(CancellationToken cancellationToken = default)
        => new(CameraPermissions.RequestAsync(_activity, cancellationToken));
}
