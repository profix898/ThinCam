namespace ThinCam.Avalonia;

/// <summary>Describes a frame publication or render notification.</summary>
public sealed class CameraPreviewFrameEventArgs : EventArgs
{
    internal CameraPreviewFrameEventArgs(
        long version,
        int pixelWidth,
        int pixelHeight,
        bool hasFrame)
    {
        Version = version;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        HasFrame = hasFrame;
    }

    /// <summary>Gets the monotonically increasing source version.</summary>
    public long Version { get; }

    /// <summary>Gets the published bitmap width, or zero when cleared.</summary>
    public int PixelWidth { get; }

    /// <summary>Gets the published bitmap height, or zero when cleared.</summary>
    public int PixelHeight { get; }

    /// <summary>Gets whether a drawable frame was available.</summary>
    public bool HasFrame { get; }
}
