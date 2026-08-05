using Android.Content.PM;
using Avalonia.Android;
using ThinCamDemo.Services;

namespace ThinCamDemo.Android;

/// <summary>Hosts the demo's main Android activity.</summary>
[Activity(Label = "ThinCam Demo",
          Theme = "@android:style/Theme.Material.Light.NoActionBar",
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
        DemoServices.Platform = new AndroidPlatformServices(this);
        base.OnCreate(savedInstanceState);
    }
}
