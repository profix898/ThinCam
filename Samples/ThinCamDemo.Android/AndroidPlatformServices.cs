using System.Runtime.InteropServices;
using Android.App;
using ThinCam;
using ThinCam.Demo.Services;

namespace ThinCam.Demo.Android;

internal sealed class AndroidPlatformServices(Activity activity) : IPlatformServices
{
    private readonly Activity _activity =
        activity ?? throw new ArgumentNullException(nameof(activity));

    public string PlatformDescription =>
        $"Android {(global::Android.OS.Build.VERSION.Release)} / " +
        $"{RuntimeInformation.ProcessArchitecture} / .NET {Environment.Version}";

    public ValueTask<CameraPermissionStatus> RequestCameraPermissionAsync(
        CancellationToken cancellationToken = default) =>
        new(CameraPermissions.RequestAsync(_activity, cancellationToken));
}
