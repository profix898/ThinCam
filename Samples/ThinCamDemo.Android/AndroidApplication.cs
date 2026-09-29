using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ThinCamDemo.Services;

namespace ThinCamDemo.Android;

/// <summary>Hosts the Avalonia application on Android.</summary>
[Application]
public sealed class AndroidApplication : AvaloniaAndroidApplication<App>
{
    /// <summary>Initializes the Android application wrapper.</summary>
    /// <param name="javaReference">The Java object reference.</param>
    /// <param name="transfer">The reference ownership transfer mode.</param>
    public AndroidApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    internal static AndroidPlatformServices PlatformServices { get; } = new AndroidPlatformServices();

    /// <inheritdoc />
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        DemoServices.Platform = PlatformServices;
        return base.CustomizeAppBuilder(builder)
                   .WithInterFont()
                   .LogToTrace();
    }
}
