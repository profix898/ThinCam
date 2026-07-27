namespace ThinCam.Demo.Services;

public static class DemoServices
{
    private static IPlatformServices _platform = new DefaultPlatformServices();

    public static IPlatformServices Platform
    {
        get => Volatile.Read(ref _platform);
        set => Volatile.Write(ref _platform, value ?? throw new ArgumentNullException(nameof(value)));
    }
}
