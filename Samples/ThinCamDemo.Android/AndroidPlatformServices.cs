using System.Runtime.InteropServices;
using Android.OS;
using ThinCam;
using ThinCamDemo.Services;
using Environment = System.Environment;

namespace ThinCamDemo.Android;

internal sealed class AndroidPlatformServices : IPlatformServices
{
    private readonly SemaphoreSlim _permissionGate = new SemaphoreSlim(1, 1);
    private WeakReference<Activity>? _activity;

    #region IPlatformServices

    /// <inheritdoc />
    public string PlatformDescription
        => $"Android {Build.VERSION.Release} / " +
           $"{RuntimeInformation.ProcessArchitecture} / .NET {Environment.Version}";

    /// <inheritdoc />
    public async ValueTask<CameraPermissionStatus> RequestCameraPermissionAsync(CancellationToken cancellationToken = default)
    {
        await _permissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activity = GetActivity();
            return await CameraPermissions.RequestAsync(activity, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _permissionGate.Release();
        }
    }

    #endregion

    internal void Attach(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Volatile.Write(ref _activity, new WeakReference<Activity>(activity));
    }

    internal void Detach(Activity activity)
    {
        var reference = Volatile.Read(ref _activity);
        if (reference?.TryGetTarget(out var current) == true && ReferenceEquals(current, activity))
            Volatile.Write(ref _activity, null);
    }

    private Activity GetActivity()
    {
        var reference = Volatile.Read(ref _activity);
        Activity? activity = null;
        if (reference?.TryGetTarget(out activity) != true || activity is null || activity.IsFinishing || activity.IsDestroyed)
            throw new InvalidOperationException("Camera permission requires an active Android activity.");
        return activity;
    }
}
