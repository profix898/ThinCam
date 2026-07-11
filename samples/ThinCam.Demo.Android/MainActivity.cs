using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using ThinCam.Demo.Services;

namespace ThinCam.Demo.Android;

[Activity(
    Label = "ThinCam Demo",
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
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        DemoServices.Platform = new AndroidPlatformServices(this);
        base.OnCreate(savedInstanceState);
    }
}
