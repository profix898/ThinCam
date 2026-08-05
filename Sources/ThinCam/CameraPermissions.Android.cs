#if ANDROID
#pragma warning disable CS0618 // Platform Fragment is used to avoid an AndroidX dependency.
#pragma warning disable CA1422
using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;

namespace ThinCam;

/// <summary>Provides Android-specific camera permission operations.</summary>
public static partial class CameraPermissions
{
    private const int AndroidRequestCode = 18441;

    private static CameraPermissionStatus GetAndroidStatus()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.M)
        {
            return CameraPermissionStatus.Granted;
        }

        return Application.Context.CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted
            ? CameraPermissionStatus.Granted
            : CameraPermissionStatus.Denied;
    }

    /// <summary>Requests camera permission through the specified Android activity.</summary>
    public static Task<CameraPermissionStatus> RequestAsync(Activity activity,
                                                            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);

        CameraPermissionStatus current = GetAndroidStatus();
        if (current == CameraPermissionStatus.Granted)
        {
            return Task.FromResult(current);
        }

        var completion = new TaskCompletionSource<CameraPermissionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationTokenRegistration registration =
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _ = completion.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration) state!).Dispose(),
                                         registration,
                                         CancellationToken.None,
                                         TaskContinuationOptions.ExecuteSynchronously,
                                         TaskScheduler.Default);

        activity.RunOnUiThread(() =>
        {
            try
            {
                var fragment = new CameraPermissionFragment(completion);
                FragmentManager manager = activity.FragmentManager
                                          ?? throw new InvalidOperationException("The activity does not have a FragmentManager.");
                FragmentTransaction transaction = manager.BeginTransaction()
                                                  ?? throw new InvalidOperationException("The FragmentManager did not start a transaction.");
                _ = transaction.Add(fragment, CameraPermissionFragment.TagName);
                transaction.CommitAllowingStateLoss();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        return completion.Task;
    }

    private sealed class CameraPermissionFragment : Fragment
    {
        internal const string TagName = "ThinCam.CameraPermission";
        private TaskCompletionSource<CameraPermissionStatus>? _completion;

        public CameraPermissionFragment()
        {
        }

        internal CameraPermissionFragment(TaskCompletionSource<CameraPermissionStatus> completion)
        {
            _completion = completion;
        }

        public override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            RequestPermissions([Manifest.Permission.Camera], AndroidRequestCode);
        }

        public override void OnRequestPermissionsResult(int requestCode,
                                                        string[] permissions,
                                                        Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode != AndroidRequestCode)
                return;

            CameraPermissionStatus status = grantResults.Length > 0 && grantResults[0] == Permission.Granted
                ? CameraPermissionStatus.Granted
                : CameraPermissionStatus.Denied;

            _completion?.TrySetResult(status);

            FragmentTransaction? transaction = FragmentManager?.BeginTransaction();
            if (transaction is not null)
            {
                _ = transaction.Remove(this);
                transaction.CommitAllowingStateLoss();
            }
        }
    }
}
#pragma warning restore CA1422
#pragma warning restore CS0618
#endif
