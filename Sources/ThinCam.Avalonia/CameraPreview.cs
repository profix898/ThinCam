using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using SkiaSharp;

namespace ThinCam.Avalonia;

/// <summary>
/// A lookless control that presents the latest frame of a <see cref="CameraPreviewSource" />
/// together with themable chrome and placeholder content.
/// </summary>
/// <remarks>
/// <para>
/// The default control theme composes a <see cref="Border" /> honouring
/// <see cref="TemplatedControl.Background" />, <see cref="TemplatedControl.BorderBrush" />,
/// <see cref="TemplatedControl.BorderThickness" />, <see cref="TemplatedControl.CornerRadius" />,
/// and <see cref="TemplatedControl.Padding" />, a <see cref="CameraPreviewSurface" /> named
/// <c>PART_Surface</c> that performs the Skia frame draw, and a <see cref="ContentPresenter" />
/// named <c>PART_Placeholder</c> for <see cref="PlaceholderContent" />.
/// </para>
/// <para>
/// Replace the template to change the composition; the only requirement for live frames is a
/// <see cref="CameraPreviewSurface" /> named <c>PART_Surface</c>. The control neither opens nor
/// owns a camera.
/// </para>
/// </remarks>
[TemplatePart(SurfacePartName, typeof(CameraPreviewSurface))]
[PseudoClasses(HasFramePseudoClass)]
public class CameraPreview : TemplatedControl
{
    private const string HasFramePseudoClass = ":has-frame";

    /// <summary>The name of the placeholder <see cref="ContentPresenter" /> template part.</summary>
    public const string PlaceholderPartName = "PART_Placeholder";

    /// <summary>The name of the <see cref="CameraPreviewSurface" /> template part.</summary>
    public const string SurfacePartName = "PART_Surface";

    /// <summary>Defines the read-only <see cref="HasFrame" /> property.</summary>
    public static readonly DirectProperty<CameraPreview, bool> HasFrameProperty =
        AvaloniaProperty.RegisterDirect<CameraPreview, bool>(nameof(HasFrame), static o => o.HasFrame);

    /// <summary>Defines the read-only <see cref="IsPlaceholderVisible" /> property.</summary>
    public static readonly DirectProperty<CameraPreview, bool> IsPlaceholderVisibleProperty =
        AvaloniaProperty.RegisterDirect<CameraPreview, bool>(nameof(IsPlaceholderVisible),
                                                             static o => o.IsPlaceholderVisible);

    /// <summary>Defines the <see cref="PlaceholderContent" /> property.</summary>
    public static readonly StyledProperty<object?> PlaceholderContentProperty =
        AvaloniaProperty.Register<CameraPreview, object?>(nameof(PlaceholderContent),
                                                          "No camera frame");

    /// <summary>Defines the <see cref="PlaceholderTemplate" /> property.</summary>
    public static readonly StyledProperty<IDataTemplate?> PlaceholderTemplateProperty =
        AvaloniaProperty.Register<CameraPreview, IDataTemplate?>(nameof(PlaceholderTemplate));

    /// <summary>Defines the <see cref="ShowPlaceholder" /> property.</summary>
    public static readonly StyledProperty<bool> ShowPlaceholderProperty =
        AvaloniaProperty.Register<CameraPreview, bool>(nameof(ShowPlaceholder), true);

    /// <summary>Defines the <see cref="Source" /> property.</summary>
    public static readonly StyledProperty<CameraPreviewSource?> SourceProperty =
        AvaloniaProperty.Register<CameraPreview, CameraPreviewSource?>(nameof(Source));

    /// <summary>Defines the <see cref="StretchDirection" /> property.</summary>
    public static readonly StyledProperty<StretchDirection> StretchDirectionProperty =
        AvaloniaProperty.Register<CameraPreview, StretchDirection>(nameof(StretchDirection),
                                                                   StretchDirection.Both);

    /// <summary>Defines the <see cref="Stretch" /> property.</summary>
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<CameraPreview, Stretch>(nameof(Stretch), Stretch.Uniform);

    private CameraPreviewSurface? _surface;
    private bool _hasFrame;
    private bool _isPlaceholderVisible = true;

    /// <summary>
    /// Gets whether the surface is currently drawing a frame. Also exposed as the
    /// <c>:has-frame</c> pseudo-class.
    /// </summary>
    public bool HasFrame
    {
        get => _hasFrame;
        private set
        {
            if (SetAndRaise(HasFrameProperty, ref _hasFrame, value))
            {
                if (value)
                    PseudoClasses.Add(HasFramePseudoClass);
                else
                    PseudoClasses.Remove(HasFramePseudoClass);
                UpdatePlaceholderVisibility();
            }
        }
    }

    /// <summary>
    /// Gets whether placeholder content should currently be visible, that is
    /// <see cref="ShowPlaceholder" /> is set and no frame is being drawn.
    /// </summary>
    public bool IsPlaceholderVisible
    {
        get => _isPlaceholderVisible;
        private set => SetAndRaise(IsPlaceholderVisibleProperty, ref _isPlaceholderVisible, value);
    }

    /// <summary>Gets or sets the content shown while no frame has been drawn.</summary>
    public object? PlaceholderContent
    {
        get => GetValue(PlaceholderContentProperty);
        set => SetValue(PlaceholderContentProperty, value);
    }

    /// <summary>Gets or sets the template used to present <see cref="PlaceholderContent" />.</summary>
    public IDataTemplate? PlaceholderTemplate
    {
        get => GetValue(PlaceholderTemplateProperty);
        set => SetValue(PlaceholderTemplateProperty, value);
    }

    /// <summary>Gets or sets whether placeholder content may be shown at all.</summary>
    public bool ShowPlaceholder
    {
        get => GetValue(ShowPlaceholderProperty);
        set => SetValue(ShowPlaceholderProperty, value);
    }

    /// <summary>Gets or sets the frame source.</summary>
    public CameraPreviewSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Gets or sets how the image is scaled into the surface bounds.</summary>
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

    /// <summary>Creates an independent snapshot of the current preview frame.</summary>
    public SKBitmap? CopySnapshot() => Source?.CopySnapshot();

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_surface is not null)
        {
            _surface.FrameRendered -= OnSurfaceFrameRendered;
            _surface.HasFrameChanged -= OnSurfaceHasFrameChanged;
        }

        _surface = e.NameScope.Find<CameraPreviewSurface>(SurfacePartName);

        if (_surface is not null)
        {
            _surface.FrameRendered += OnSurfaceFrameRendered;
            _surface.HasFrameChanged += OnSurfaceHasFrameChanged;
        }

        HasFrame = _surface?.HasFrame ?? false;
        UpdatePlaceholderVisibility();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ShowPlaceholderProperty)
            UpdatePlaceholderVisibility();
    }

    private void OnSurfaceFrameRendered(object? sender, CameraPreviewFrameEventArgs e) => FrameRendered?.Invoke(this, e);

    private void OnSurfaceHasFrameChanged(object? sender, EventArgs e) => HasFrame = _surface?.HasFrame ?? false;

    private void UpdatePlaceholderVisibility() => IsPlaceholderVisible = ShowPlaceholder && !HasFrame;
}
