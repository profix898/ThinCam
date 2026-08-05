using SkiaSharp;
using ThinCam.SkiaSharp;

namespace ThinCam.Avalonia;

/// <summary>
/// Thread-safe presentation source used by <see cref="CameraPreview" />.
/// Capture code may publish frames from a worker thread while Avalonia renders
/// the most recently published frame on its UI/render thread.
/// </summary>
public sealed class CameraPreviewSource : IDisposable
{
    private readonly SkiaFrameBuffer _buffer = new();
    private int _hasFrame;
    private int _disposed;

    /// <summary>
    /// Raised after a new frame is published or the source is cleared. Handlers
    /// can be invoked on the publishing thread and should return quickly.
    /// </summary>
    public event EventHandler<CameraPreviewFrameEventArgs>? FrameChanged;

    /// <summary>Gets the current source version.</summary>
    public long Version => _buffer.Version;

    /// <summary>Gets whether a drawable frame is currently available.</summary>
    public bool HasFrame => Volatile.Read(ref _hasFrame) != 0;

    /// <summary>
    /// Copies a ThinCam frame into reusable Skia-owned memory and publishes it.
    /// The caller retains ownership of <paramref name="frame" /> and may dispose it
    /// immediately after this method returns.
    /// </summary>
    public void Publish(VideoFrame frame,
                        SkiaFrameTransform transform = SkiaFrameTransform.Presentation)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfDisposed();

        SKSizeI size = frame.GetSkiaSize(transform);
        _buffer.Update(frame, transform);
        Volatile.Write(ref _hasFrame, 1);
        RaiseFrameChanged(new CameraPreviewFrameEventArgs(_buffer.Version,
                                                          size.Width,
                                                          size.Height,
                                                          true));
    }

    /// <summary>Removes the currently published frame.</summary>
    public void Clear()
    {
        ThrowIfDisposed();
        _buffer.Clear();
        Volatile.Write(ref _hasFrame, 0);
        RaiseFrameChanged(new CameraPreviewFrameEventArgs(_buffer.Version,
                                                          0,
                                                          0,
                                                          false));
    }

    /// <summary>
    /// Creates an independent copy of the current preview bitmap. The caller
    /// owns and must dispose the returned bitmap.
    /// </summary>
    public SKBitmap? CopySnapshot()
    {
        ThrowIfDisposed();
        return _buffer.CopySnapshot();
    }

    internal bool TryUse(Action<SKBitmap, long> reader)
    {
        ThrowIfDisposed();
        return _buffer.TryUse(reader);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _hasFrame, 0);
        _buffer.Dispose();
        FrameChanged = null;
    }

    private void RaiseFrameChanged(CameraPreviewFrameEventArgs eventArgs)
    {
        var handlers = FrameChanged;
        if (handlers is null)
            return;

        // A UI notification must never break the capture loop. Invoke handlers
        // independently so one faulty subscriber does not suppress the others.
        foreach (EventHandler<CameraPreviewFrameEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch
            {
                // Notification failures are isolated from frame publication.
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
