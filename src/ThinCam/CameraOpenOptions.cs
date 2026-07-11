namespace ThinCam;

public sealed record CameraOpenOptions
{
    public int Width { get; init; } = 640;
    public int Height { get; init; } = 480;
    public int FramesPerSecond { get; init; } = 30;
    public PixelFormat PixelFormat { get; init; } = PixelFormat.Bgra32;

    /// <summary>
    /// Number of managed frames retained while the consumer is behind. Old frames are disposed first.
    /// </summary>
    public int QueueCapacity { get; init; } = 2;

    internal void Validate()
    {
        if (Width <= 0) throw new ArgumentOutOfRangeException(nameof(Width));
        if (Height <= 0) throw new ArgumentOutOfRangeException(nameof(Height));
        if (FramesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(FramesPerSecond));
        if (QueueCapacity is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        if (PixelFormat != PixelFormat.Bgra32) throw new NotSupportedException("ThinCam currently outputs BGRA32 only.");
    }
}
