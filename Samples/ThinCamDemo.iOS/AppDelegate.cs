using Avalonia;
using Avalonia.iOS;

namespace ThinCamDemo.iOS;

/// <summary>Hosts the Avalonia application on iOS.</summary>
[Register("AppDelegate")]
#pragma warning disable CA1711
public class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711
{
    /// <inheritdoc />
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}
