using System.Buffers;

namespace ThinCam;

/// <summary>Represents an owned video frame captured by a camera.</summary>
public sealed class VideoFrame : IDisposable
{
    private IMemoryOwner<byte>? _owner;

    internal VideoFrame(IMemoryOwner<byte> owner,
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

    /// <summary>Gets the frame's pixel data.</summary>
    public ReadOnlyMemory<byte> Data
        => _owner?.Memory[..DataLength]
           ?? throw new ObjectDisposedException(nameof(VideoFrame));

    /// <summary>Gets the valid pixel-data length in bytes.</summary>
    public int DataLength { get; }

    /// <summary>Gets the frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets whether presentation should mirror the frame horizontally.</summary>
    public bool IsMirrored { get; }

    /// <summary>Gets the frame's pixel format.</summary>
    public PixelFormat PixelFormat { get; }

    /// <summary>
    /// Best-effort clockwise orientation metadata. Android reports sensor orientation;
    /// Apple and desktop backends currently report zero.
    /// </summary>
    public int RotationDegrees { get; }

    /// <summary>Gets the number of bytes between adjacent pixel rows.</summary>
    public int Stride { get; }

    /// <summary>Gets the capture timestamp reported by the backend.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>Gets the frame width in pixels.</summary>
    public int Width { get; }

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.Dispose();
    }

    #endregion
}
