using SkiaSharp;
using ThinCam;

namespace ThinCam.SkiaSharp;

/// <summary>
/// Maintains two reusable Skia-owned bitmaps for a live preview pipeline.
/// Camera updates copy into a hidden back buffer while readers continue using
/// the published front buffer; only the final buffer swap takes a write lock.
/// </summary>
public sealed class SkiaFrameBuffer : IDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly object _updateGate = new();
    private readonly ReaderWriterLockSlim _frontLock =
        new(LockRecursionPolicy.NoRecursion);
    private SKBitmap? _front;
    private SKBitmap? _back;
    private long _version;
    private int _activeOperations;
    private bool _disposeRequested;
    private int _disposeSignaled;

    /// <summary>Increases after each successfully published frame.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Copies a frame into the reusable back buffer and publishes it as the new
    /// front buffer. Only one update is processed at a time.
    /// </summary>
    public void Update(
        VideoFrame frame,
        SkiaFrameTransform transform = SkiaFrameTransform.None)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnterOperation();

        try
        {
            SKSizeI size = frame.GetSkiaSize(transform);

            lock (_updateGate)
            {
                EnsureBackBuffer(size.Width, size.Height);

                // Readers cannot observe _back. Keep the relatively expensive
                // frame copy outside the front-buffer write lock.
                frame.CopyTo(_back!, transform);

                _frontLock.EnterWriteLock();
                try
                {
                    (_front, _back) = (_back, _front);
                    Interlocked.Increment(ref _version);
                }
                finally
                {
                    _frontLock.ExitWriteLock();
                }
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Removes both reusable bitmaps and advances <see cref="Version"/>. Readers
    /// in progress finish before their bitmap is released.
    /// </summary>
    public void Clear()
    {
        EnterOperation();

        try
        {
            lock (_updateGate)
            {
                _frontLock.EnterWriteLock();
                try
                {
                    _front?.Dispose();
                    _front = null;
                    _back?.Dispose();
                    _back = null;
                    Interlocked.Increment(ref _version);
                }
                finally
                {
                    _frontLock.ExitWriteLock();
                }
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Invokes <paramref name="reader"/> with the current front bitmap while it
    /// is protected from concurrent reuse. The bitmap must not be retained after
    /// the callback returns. The callback must not call <see cref="Update"/> or
    /// <see cref="Dispose"/> on this buffer.
    /// </summary>
    public bool TryUse(Action<SKBitmap> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return TryUse((bitmap, _) => reader(bitmap));
    }

    /// <summary>
    /// Invokes <paramref name="reader"/> with the current front bitmap and the
    /// version that published that exact bitmap while both are protected by the
    /// same read lease. The bitmap must not be retained after the callback.
    /// </summary>
    public bool TryUse(Action<SKBitmap, long> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        EnterOperation();

        try
        {
            _frontLock.EnterReadLock();
            try
            {
                if (_front is null)
                {
                    return false;
                }

                reader(_front, Interlocked.Read(ref _version));
                return true;
            }
            finally
            {
                _frontLock.ExitReadLock();
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Draws the current frame into <paramref name="destination"/> while holding
    /// a read lease. Returns <see langword="false"/> before the first frame arrives.
    /// </summary>
    public bool TryDraw(
        SKCanvas canvas,
        SKRect destination,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        return TryUse(bitmap => canvas.DrawBitmap(
            bitmap,
            destination,
            SKSamplingOptions.Default,
            paint));
    }

    /// <summary>
    /// Creates an independent bitmap snapshot of the currently published frame.
    /// The caller owns the returned bitmap.
    /// </summary>
    public SKBitmap? CopySnapshot()
    {
        SKBitmap? snapshot = null;
        TryUse(bitmap =>
        {
            snapshot = bitmap.Copy()
                ?? throw new InvalidOperationException(
                    "SkiaSharp could not copy the preview bitmap.");
        });
        return snapshot;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            _disposeRequested = true;
            while (_activeOperations != 0)
            {
                Monitor.Wait(_lifecycleGate);
            }
        }

        _front?.Dispose();
        _front = null;
        _back?.Dispose();
        _back = null;
        _frontLock.Dispose();
    }

    private void EnterOperation()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _activeOperations++;
        }
    }

    private void ExitOperation()
    {
        lock (_lifecycleGate)
        {
            _activeOperations--;
            if (_disposeRequested && _activeOperations == 0)
            {
                Monitor.PulseAll(_lifecycleGate);
            }
        }
    }

    private void EnsureBackBuffer(int width, int height)
    {
        if (_back is not null &&
            _back.Width == width &&
            _back.Height == height &&
            _back.ColorType == SKColorType.Bgra8888)
        {
            return;
        }

        _back?.Dispose();
        _back = new SKBitmap(
            VideoFrameSkiaExtensions.CreateImageInfo(width, height));
    }
}
