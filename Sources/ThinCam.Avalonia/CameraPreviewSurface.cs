using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace ThinCam.Avalonia;

/// <summary>
/// Draws the latest frame of a <see cref="CameraPreviewSource" /> straight onto Avalonia's
/// Skia canvas.
/// </summary>
/// <remarks>
/// This is the rendering part of <see cref="CameraPreview" /> and is expected to appear in a
/// <see cref="CameraPreview" /> control template named <c>PART_Surface</c>. It draws nothing
/// but the frame itself; background, border, and placeholder chrome are the responsibility of
/// the surrounding template. The control neither opens nor owns a camera.
/// </remarks>
public class CameraPreviewSurface : Control
{
    /// <summary>Defines the read-only <see cref="HasFrame" /> property.</summary>
    public static readonly DirectProperty<CameraPreviewSurface, bool> HasFrameProperty =
        AvaloniaProperty.RegisterDirect<CameraPreviewSurface, bool>(nameof(HasFrame),
                                                                    static o => o.HasFrame);

    /// <summary>Defines the <see cref="Source" /> property.</summary>
    public static readonly StyledProperty<CameraPreviewSource?> SourceProperty =
        AvaloniaProperty.Register<CameraPreviewSurface, CameraPreviewSource?>(nameof(Source));

    /// <summary>Defines the <see cref="StretchDirection" /> property.</summary>
    public static readonly StyledProperty<StretchDirection> StretchDirectionProperty =
        AvaloniaProperty.Register<CameraPreviewSurface, StretchDirection>(nameof(StretchDirection),
                                                                          StretchDirection.Both);

    /// <summary>Defines the <see cref="Stretch" /> property.</summary>
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<CameraPreviewSurface, Stretch>(nameof(Stretch),
                                                                 Stretch.Uniform);

    private CameraPreviewSource? _subscribedSource;
    private int _invalidatePending;
    private bool _isAttached;
    private bool _hasFrame;
    private long _lastRenderedVersion = -1;

    /// <summary>Gets whether the most recent render pass drew a frame.</summary>
    public bool HasFrame
    {
        get => _hasFrame;
        private set
        {
            if (SetAndRaise(HasFrameProperty, ref _hasFrame, value))
                HasFrameChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Gets or sets the frame source.</summary>
    public CameraPreviewSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Gets or sets how the image is scaled into the control bounds.</summary>
    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>Gets or sets whether scaling may enlarge, shrink, or do both.</summary>
    public StretchDirection StretchDirection
    {
        get => GetValue(StretchDirectionProperty);
        set => SetValue(StretchDirectionProperty, value);
    }

    /// <summary>Raised on the UI thread after a new source frame version has been drawn.</summary>
    public event EventHandler<CameraPreviewFrameEventArgs>? FrameRendered;

    /// <summary>Raised on the UI thread when <see cref="HasFrame" /> changes.</summary>
    public event EventHandler? HasFrameChanged;

    /// <summary>Creates an independent snapshot of the current preview frame.</summary>
    public SKBitmap? CopySnapshot() => Source?.CopySnapshot();

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        Rect bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        context.Custom(new PreviewDrawOperation(bounds,
                                                Source,
                                                Stretch,
                                                StretchDirection,
                                                OnRenderCompleted));
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            Interlocked.Exchange(ref _lastRenderedVersion, -1);
            AttachSource(_isAttached ? Source : null);
        }

        if (change.Property == SourceProperty ||
            change.Property == StretchProperty ||
            change.Property == StretchDirectionProperty)
            InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        AttachSource(Source);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        AttachSource(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachSource(CameraPreviewSource? source)
    {
        if (ReferenceEquals(_subscribedSource, source))
            return;

        if (_subscribedSource is not null)
            _subscribedSource.FrameChanged -= OnSourceFrameChanged;

        _subscribedSource = source;

        if (_subscribedSource is not null)
            _subscribedSource.FrameChanged += OnSourceFrameChanged;
    }

    private void OnSourceFrameChanged(object? sender, CameraPreviewFrameEventArgs eventArgs) => RequestRender();

    private void RequestRender()
    {
        if (Interlocked.Exchange(ref _invalidatePending, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
                                 {
                                     Interlocked.Exchange(ref _invalidatePending, 0);
                                     if (_isAttached)
                                         InvalidateVisual();
                                 },
                                 DispatcherPriority.Render);
    }

    // Invoked from the render thread once per render pass, whether or not a frame was drawn.
    private void OnRenderCompleted(CameraPreviewFrameEventArgs eventArgs)
    {
        if (!eventArgs.HasFrame)
        {
            Dispatcher.UIThread.Post(() => HasFrame = false, DispatcherPriority.Background);
            return;
        }

        var previous = Interlocked.Exchange(ref _lastRenderedVersion, eventArgs.Version);

        Dispatcher.UIThread.Post(() =>
                                 {
                                     HasFrame = true;
                                     if (previous != eventArgs.Version)
                                         FrameRendered?.Invoke(this, eventArgs);
                                 },
                                 DispatcherPriority.Background);
    }

    #region Nested: PreviewDrawOperation

    private sealed class PreviewDrawOperation(Rect bounds,
                                              CameraPreviewSource? source,
                                              Stretch stretch,
                                              StretchDirection stretchDirection,
                                              Action<CameraPreviewFrameEventArgs> completed) : ICustomDrawOperation
    {
        #region ICustomDrawOperation

        public Rect Bounds { get; } = bounds;

        public void Dispose()
        {
        }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point point) => Bounds.Contains(point);

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
                return;

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var saveCount = canvas.Save();

            long version = 0;
            var width = 0;
            var height = 0;
            var drewFrame = false;

            try
            {
                canvas.ClipRect(ToSkRect(Bounds));

                if (source is not null)
                {
                    try
                    {
                        drewFrame = source.TryUse((bitmap, publishedVersion) =>
                        {
                            version = publishedVersion;
                            width = bitmap.Width;
                            height = bitmap.Height;
                            var destination = CalculateDestination(Bounds,
                                                                   bitmap.Width,
                                                                   bitmap.Height,
                                                                   stretch,
                                                                   stretchDirection);
                            canvas.DrawBitmap(bitmap,
                                              destination,
                                              SKSamplingOptions.Default);
                        });
                    }
                    catch (ObjectDisposedException)
                    {
                        drewFrame = false;
                    }
                }
            }
            finally
            {
                canvas.RestoreToCount(saveCount);
            }

            completed(new CameraPreviewFrameEventArgs(version, width, height, drewFrame));
        }

        #endregion

        private static SKRect CalculateDestination(Rect bounds,
                                                   int sourceWidth,
                                                   int sourceHeight,
                                                   Stretch stretch,
                                                   StretchDirection direction)
        {
            // Derive axis scales before applying stretch and direction constraints.
            var scaleX = bounds.Width / sourceWidth;
            var scaleY = bounds.Height / sourceHeight;

            switch (stretch)
            {
                case Stretch.None:
                    scaleX = 1;
                    scaleY = 1;
                    break;
                case Stretch.Fill:
                    break;
                case Stretch.Uniform:
                    scaleX = scaleY = Math.Min(scaleX, scaleY);
                    break;
                case Stretch.UniformToFill:
                    scaleX = scaleY = Math.Max(scaleX, scaleY);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stretch));
            }

            switch (direction)
            {
                case StretchDirection.UpOnly:
                    scaleX = Math.Max(1, scaleX);
                    scaleY = Math.Max(1, scaleY);
                    break;
                case StretchDirection.DownOnly:
                    scaleX = Math.Min(1, scaleX);
                    scaleY = Math.Min(1, scaleY);
                    break;
                case StretchDirection.Both:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(direction));
            }

            var width = (float) (sourceWidth * scaleX);
            var height = (float) (sourceHeight * scaleY);
            var left = (float) (bounds.X + ((bounds.Width - width) / 2));
            var top = (float) (bounds.Y + ((bounds.Height - height) / 2));
            return new SKRect(left, top, left + width, top + height);
        }

        private static SKRect ToSkRect(Rect rect) => new SKRect((float) rect.X, (float) rect.Y, (float) rect.Right, (float) rect.Bottom);
    }

    #endregion
}
