using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ThinCam.Demo;

namespace ThinCam.Demo.Android;

[Application]
public sealed class AndroidApplication : AvaloniaAndroidApplication<App>
{
    public AndroidApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}
