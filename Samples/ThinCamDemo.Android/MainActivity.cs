using System.Diagnostics;
using Android.Content.PM;
using Avalonia.Android;

namespace ThinCamDemo.Android;

/// <summary>Hosts the demo's main Android activity.</summary>
[Activity(Label = "ThinCam Demo",
          Theme = "@style/ThinCamTheme",
          Icon = "@drawable/icon",
          MainLauncher = true,
          Exported = true,
          ConfigurationChanges =
              ConfigChanges.Orientation |
              ConfigChanges.ScreenSize |
              ConfigChanges.UiMode |
              ConfigChanges.ScreenLayout |
              ConfigChanges.SmallestScreenSize |
              ConfigChanges.Density)]
public sealed class MainActivity : AvaloniaMainActivity
{
    /// <inheritdoc />
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        AndroidApplication.PlatformServices.Attach(this);
        base.OnCreate(savedInstanceState);
    }

    /// <inheritdoc />
    protected override void OnResume()
    {
        AndroidApplication.PlatformServices.Attach(this);
        base.OnResume();
    }

    /// <inheritdoc />
    protected override void OnDestroy()
    {
        AndroidApplication.PlatformServices.Detach(this);

        // Android destroys activities independently of the process, and each activity receives its
        // own view from MainViewFactory, so the camera session must be released here.
        if (Content is IAsyncDisposable view)
            _ = DisposeViewAsync(view);

        base.OnDestroy();
    }

    private static async Task DisposeViewAsync(IAsyncDisposable view)
    {
        try
        {
            await view.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Could not dispose the Android main view: {exception}");
        }
    }
}
