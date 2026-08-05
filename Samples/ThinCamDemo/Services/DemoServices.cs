namespace ThinCamDemo.Services;

/// <summary>Provides services shared by the demo application.</summary>
public static class DemoServices
{
    private static IPlatformServices _platform = new DefaultPlatformServices();

    /// <summary>Gets or sets the active platform service implementation.</summary>
    public static IPlatformServices Platform
    {
        get => Volatile.Read(ref _platform);
        set => Volatile.Write(ref _platform, value ?? throw new ArgumentNullException(nameof(value)));
    }
}
