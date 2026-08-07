namespace ThinCam;

/// <summary>
/// Configures the format and buffering used when opening a camera.
/// </summary>
public sealed record CameraOpenOptions
{
    /// <summary>
    /// Gets the requested frame rate in frames per second.
    /// </summary>
    public int FramesPerSecond { get; init; } = 30;

    /// <summary>
    /// Gets the requested frame height in pixels.
    /// </summary>
    public int Height { get; init; } = 480;

    /// <summary>
    /// Gets the requested pixel format.
    /// </summary>
    public PixelFormat PixelFormat { get; init; } = PixelFormat.Bgra32;

    /// <summary>
    /// Number of managed frames retained while the consumer is behind. Old frames are disposed first.
    /// </summary>
    public int QueueCapacity { get; init; } = 2;

    /// <summary>
    /// Gets the requested frame width in pixels.
    /// </summary>
    public int Width { get; init; } = 640;

    internal void Validate()
    {
        if (Width <= 0)
            throw new ArgumentOutOfRangeException(nameof(Width));
        if (Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(Height));
        if (FramesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(FramesPerSecond));
        if (QueueCapacity is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        if (PixelFormat != PixelFormat.Bgra32)
            throw new NotSupportedException("ThinCam currently outputs BGRA32 only.");
    }
}
