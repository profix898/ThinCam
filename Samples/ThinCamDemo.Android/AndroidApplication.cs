using Android.Runtime;
using Avalonia;
using Avalonia.Android;

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

    /// <inheritdoc />
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}
