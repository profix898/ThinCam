using System.Buffers;

namespace ThinCam;

public sealed class VideoFrame : IDisposable
{
    private IMemoryOwner<byte>? _owner;

    internal VideoFrame(
        IMemoryOwner<byte> owner,
        int dataLength,
        int width,
        int height,
        int stride,
        PixelFormat pixelFormat,
        int rotationDegrees,
        bool isMirrored,
        TimeSpan timestamp)
    {
        _owner = owner;
        DataLength = dataLength;
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = pixelFormat;
        RotationDegrees = rotationDegrees;
        IsMirrored = isMirrored;
        Timestamp = timestamp;
    }

    public ReadOnlyMemory<byte> Data =>
        _owner?.Memory[..DataLength]
        ?? throw new ObjectDisposedException(nameof(VideoFrame));

    public int DataLength { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelFormat PixelFormat { get; }
    /// <summary>
    /// Best-effort clockwise orientation metadata. Android reports sensor orientation;
    /// Apple and desktop backends currently report zero.
    /// </summary>
    public int RotationDegrees { get; }

    public bool IsMirrored { get; }
    public TimeSpan Timestamp { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.Dispose();
    }
}
