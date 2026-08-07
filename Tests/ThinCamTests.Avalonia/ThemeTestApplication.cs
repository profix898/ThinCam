using Avalonia;
using Avalonia.Themes.Fluent;
using ThinCam.Avalonia;

namespace ThinCamTests.Avalonia;

/// <summary>The headless application that hosts the ThinCam control themes under test.</summary>
public sealed class ThemeTestApplication : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new ThinCamTheme());
    }
}
