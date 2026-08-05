using Avalonia;

namespace ThinCamDemo.Desktop;

internal static class Program
{
    /// <summary>Starts the desktop application.</summary>
    /// <param name="args">Command-line arguments passed to Avalonia.</param>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Creates the configured Avalonia application builder.</summary>
    /// <returns>The configured application builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
                     .WithInterFont()
                     .LogToTrace();
}
