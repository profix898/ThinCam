using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace ThinCam.Avalonia;

/// <summary>
/// Renders the latest frame from a <see cref="CameraPreviewSource"/> directly to
/// Avalonia's Skia canvas. The control does not open or own a camera.
/// </summary>
public sealed class CameraPreview : Control
{
    /// <summary>Defines the <see cref="Source"/> property.</summary>
    public static readonly StyledProperty<CameraPreviewSource?> SourceProperty =
        AvaloniaProperty.Register<CameraPreview, CameraPreviewSource?>(nameof(Source));

    /// <summary>Defines the <see cref="Stretch"/> property.</summary>
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<CameraPreview, Stretch>(
            nameof(Stretch),
            defaultValue: Stretch.Uniform);

    /// <summary>Defines the <see cref="StretchDirection"/> property.</summary>
    public static readonly StyledProperty<StretchDirection> StretchDirectionProperty =
        AvaloniaProperty.Register<CameraPreview, StretchDirection>(
            nameof(StretchDirection),
            defaultValue: StretchDirection.Both);

    /// <summary>Defines the <see cref="PreviewBackground"/> property.</summary>
    public static readonly StyledProperty<Color> PreviewBackgroundProperty =
        AvaloniaProperty.Register<CameraPreview, Color>(
            nameof(PreviewBackground),
            defaultValue: Color.FromRgb(16, 18, 22));

    /// <summary>Defines the <see cref="PlaceholderForeground"/> property.</summary>
    public static readonly StyledProperty<Color> PlaceholderForegroundProperty =
        AvaloniaProperty.Register<CameraPreview, Color>(
            nameof(PlaceholderForeground),
            defaultValue: Color.FromRgb(180, 184, 192));

    /// <summary>Defines the <see cref="PlaceholderText"/> property.</summary>
    public static readonly StyledProperty<string> PlaceholderTextProperty =
        AvaloniaProperty.Register<CameraPreview, string>(
            nameof(PlaceholderText),
            defaultValue: "No camera frame");

    /// <summary>Defines the <see cref="ShowPlaceholder"/> property.</summary>
    public static readonly StyledProperty<bool> ShowPlaceholderProperty =
        AvaloniaProperty.Register<CameraPreview, bool>(
            nameof(ShowPlaceholder),
            defaultValue: true);

    private CameraPreviewSource? _subscribedSource;
    private int _invalidatePending;
    private bool _isAttached;
    private long _lastRenderedVersion = -1;

    /// <summary>Raised after a source frame is rendered.</summary>
    public event EventHandler<CameraPreviewFrameEventArgs>? FrameRendered;

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

    /// <summary>Gets or sets the preview background color.</summary>
    public Color PreviewBackground
    {
        get => GetValue(PreviewBackgroundProperty);
        set => SetValue(PreviewBackgroundProperty, value);
    }

    /// <summary>Gets or sets the placeholder text color.</summary>
    public Color PlaceholderForeground
    {
        get => GetValue(PlaceholderForegroundProperty);
        set => SetValue(PlaceholderForegroundProperty, value);
    }

    /// <summary>Gets or sets the text displayed before a frame is available.</summary>
    public string PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Gets or sets whether placeholder text is rendered.</summary>
    public bool ShowPlaceholder
    {
        get => GetValue(ShowPlaceholderProperty);
        set => SetValue(ShowPlaceholderProperty, value);
    }

    /// <summary>Creates an independent snapshot of the current preview frame.</summary>
    public SKBitmap? CopySnapshot() => Source?.CopySnapshot();

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        context.Custom(new PreviewDrawOperation(
            bounds,
            Source,
            Stretch,
            StretchDirection,
            PreviewBackground,
            PlaceholderForeground,
            PlaceholderText,
            ShowPlaceholder,
            OnFrameRendered));
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
            change.Property == StretchDirectionProperty ||
            change.Property == PreviewBackgroundProperty ||
            change.Property == PlaceholderForegroundProperty ||
            change.Property == PlaceholderTextProperty ||
            change.Property == ShowPlaceholderProperty)
        {
            InvalidateVisual();
        }
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
        {
            return;
        }

        if (_subscribedSource is not null)
        {
            _subscribedSource.FrameChanged -= OnSourceFrameChanged;
        }

        _subscribedSource = source;

        if (_subscribedSource is not null)
        {
            _subscribedSource.FrameChanged += OnSourceFrameChanged;
        }
    }

    private void OnSourceFrameChanged(object? sender, CameraPreviewFrameEventArgs eventArgs) =>
        RequestRender();

    private void RequestRender()
    {
        if (Interlocked.Exchange(ref _invalidatePending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                Interlocked.Exchange(ref _invalidatePending, 0);
                if (_isAttached)
                {
                    InvalidateVisual();
                }
            },
            DispatcherPriority.Render);
    }

    private void OnFrameRendered(CameraPreviewFrameEventArgs eventArgs)
    {
        if (!eventArgs.HasFrame)
        {
            return;
        }

        long previous = Interlocked.Exchange(
            ref _lastRenderedVersion,
            eventArgs.Version);
        if (previous == eventArgs.Version)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => FrameRendered?.Invoke(this, eventArgs),
            DispatcherPriority.Background);
    }

    private sealed class PreviewDrawOperation(
        Rect bounds,
        CameraPreviewSource? source,
        Stretch stretch,
        StretchDirection stretchDirection,
        Color background,
        Color placeholderForeground,
        string placeholderText,
        bool showPlaceholder,
        Action<CameraPreviewFrameEventArgs> rendered) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public void Dispose()
        {
        }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point point) => Bounds.Contains(point);

        public void Render(ImmediateDrawingContext context)
        {
            ISkiaSharpApiLeaseFeature? feature =
                context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
            {
                return;
            }

            using ISkiaSharpApiLease lease = feature.Lease();
            SKCanvas canvas = lease.SkCanvas;
            int saveCount = canvas.Save();

            try
            {
                SKRect clip = ToSkRect(Bounds);
                canvas.ClipRect(clip);
                canvas.DrawColor(ToSkColor(background));

                long version = 0;
                int width = 0;
                int height = 0;
                bool drewFrame = false;

                if (source is not null)
                {
                    try
                    {
                        drewFrame = source.TryUse((bitmap, publishedVersion) =>
                        {
                            version = publishedVersion;
                            width = bitmap.Width;
                            height = bitmap.Height;
                            SKRect destination = CalculateDestination(
                                Bounds,
                                bitmap.Width,
                                bitmap.Height,
                                stretch,
                                stretchDirection);
                            canvas.DrawBitmap(
                                bitmap,
                                destination,
                                SKSamplingOptions.Default,
                                null);
                        });
                    }
                    catch (ObjectDisposedException)
                    {
                        drewFrame = false;
                    }
                }

                if (drewFrame)
                {
                    rendered(new CameraPreviewFrameEventArgs(
                        version,
                        width,
                        height,
                        hasFrame: true));
                }
                else if (showPlaceholder && !string.IsNullOrWhiteSpace(placeholderText))
                {
                    DrawPlaceholder(canvas, Bounds, placeholderText, placeholderForeground);
                }
            }
            finally
            {
                canvas.RestoreToCount(saveCount);
            }
        }

        private static void DrawPlaceholder(
            SKCanvas canvas,
            Rect bounds,
            string text,
            Color color)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = ToSkColor(color)
            };
            using var font = new SKFont(SKTypeface.Default, 18);

            float textWidth = font.MeasureText(text, paint);
            SKFontMetrics metrics = font.Metrics;
            float x = (float)(bounds.X + Math.Max(12, (bounds.Width - textWidth) / 2));
            float y = (float)(bounds.Y + (bounds.Height - metrics.Ascent - metrics.Descent) / 2);
            canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
        }

        private static SKRect CalculateDestination(
            Rect bounds,
            int sourceWidth,
            int sourceHeight,
            Stretch stretch,
            StretchDirection direction)
        {
            double scaleX = bounds.Width / sourceWidth;
            double scaleY = bounds.Height / sourceHeight;

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

            float width = (float)(sourceWidth * scaleX);
            float height = (float)(sourceHeight * scaleY);
            float left = (float)(bounds.X + (bounds.Width - width) / 2);
            float top = (float)(bounds.Y + (bounds.Height - height) / 2);
            return new SKRect(left, top, left + width, top + height);
        }

        private static SKColor ToSkColor(Color color) =>
            new(color.R, color.G, color.B, color.A);

        private static SKRect ToSkRect(Rect rect) =>
            new((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);
    }
}
