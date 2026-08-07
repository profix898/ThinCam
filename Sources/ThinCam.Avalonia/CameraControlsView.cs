using System.Collections;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ThinCam.Avalonia;

/// <summary>
/// A lookless control that presents and edits the exposure, focus, zoom, and light controls of
/// a <see cref="ThinCam.Camera" />.
/// </summary>
/// <remarks>
/// <para>
/// All state is exposed as styled and direct properties on the control itself, so a control
/// template binds through <c>{TemplateBinding}</c> (one way) or
/// <c>{Binding …, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}</c> (two way).
/// The control does not use a view model and does not depend on <c>DataContext</c>.
/// </para>
/// <para>
/// Setting <see cref="Camera" /> loads the camera capabilities and current control state.
/// Capabilities the camera does not report are surfaced both as read-only <c>Has…</c>
/// properties and as pseudo-classes, so a template can hide unsupported sections with either a
/// binding or a style selector such as
/// <c>CameraControlsView:has-zoom /template/ Border#PART_ZoomSection</c>.
/// </para>
/// <para>
/// Slider and numeric edits are applied to the camera after a short debounce interval; mode
/// and light toggles are applied immediately. Every apply raises either
/// <see cref="StatusChanged" /> or <see cref="ErrorOccurred" />.
/// </para>
/// </remarks>
[PseudoClasses(HasCameraClass,
               HasExposureModesClass,
               HasExposureCompensationClass,
               HasManualExposureClass,
               HasIsoClass,
               HasFocusModesClass,
               HasManualFocusClass,
               HasZoomClass,
               HasLightClass,
               HasVariableLightClass,
               ExposureManualClass,
               FocusManualClass,
               HasErrorClass)]
public class CameraControlsView : TemplatedControl, INotifyDataErrorInfo
{
    private const string ExposureManualClass = ":exposure-manual";
    private const string FocusManualClass = ":focus-manual";
    private const string HasErrorClass = ":has-error";
    private const string HasCameraClass = ":has-camera";
    private const string HasExposureCompensationClass = ":has-exposure-compensation";
    private const string HasExposureModesClass = ":has-exposure-modes";
    private const string HasFocusModesClass = ":has-focus-modes";
    private const string HasIsoClass = ":has-iso";
    private const string HasLightClass = ":has-light";
    private const string HasManualExposureClass = ":has-manual-exposure";
    private const string HasManualFocusClass = ":has-manual-focus";
    private const string HasVariableLightClass = ":has-variable-light";
    private const string HasZoomClass = ":has-zoom";

    /// <summary>Defines the <see cref="ApplyDelay" /> property.</summary>
    public static readonly StyledProperty<TimeSpan> ApplyDelayProperty =
        AvaloniaProperty.Register<CameraControlsView, TimeSpan>(nameof(ApplyDelay),
                                                                TimeSpan.FromMilliseconds(300));

    // --- Input properties ---

    /// <summary>Defines the <see cref="Camera" /> property.</summary>
    public static readonly StyledProperty<Camera?> CameraProperty =
        AvaloniaProperty.Register<CameraControlsView, Camera?>(nameof(Camera));

    // --- Routed events ---

    /// <summary>Defines the <see cref="ErrorOccurred" /> event.</summary>
    public static readonly RoutedEvent<CameraControlsMessageEventArgs> ErrorOccurredEvent =
        RoutedEvent.Register<CameraControlsView, CameraControlsMessageEventArgs>(nameof(ErrorOccurred),
                                                                                 RoutingStrategies.Bubble);

    /// <summary>Defines the read-only <see cref="ExposureCompensationMaximum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> ExposureCompensationMaximumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(ExposureCompensationMaximum),
                                                                    static o => o.ExposureCompensationMaximum);

    /// <summary>Defines the read-only <see cref="ExposureCompensationMinimum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> ExposureCompensationMinimumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(ExposureCompensationMinimum),
                                                                    static o => o.ExposureCompensationMinimum);

    /// <summary>Defines the <see cref="ExposureCompensation" /> property.</summary>
    public static readonly StyledProperty<double> ExposureCompensationProperty =
        AvaloniaProperty.Register<CameraControlsView, double>(nameof(ExposureCompensation),
                                                              defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines the read-only <see cref="ExposureCompensationStep" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> ExposureCompensationStepProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(ExposureCompensationStep),
                                                                    static o => o.ExposureCompensationStep);

    // --- Read-only capability descriptions ---

    /// <summary>Defines the read-only <see cref="ExposureModes" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, IReadOnlyList<ExposureMode>> ExposureModesProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, IReadOnlyList<ExposureMode>>(nameof(ExposureModes),
                                                                                         static o => o.ExposureModes);

    /// <summary>Defines the read-only <see cref="FocusModes" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, IReadOnlyList<FocusMode>> FocusModesProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, IReadOnlyList<FocusMode>>(nameof(FocusModes),
                                                                                      static o => o.FocusModes);

    /// <summary>Defines the <see cref="FocusPosition" /> property.</summary>
    public static readonly StyledProperty<double> FocusPositionProperty =
        AvaloniaProperty.Register<CameraControlsView, double>(nameof(FocusPosition),
                                                              0.5,
                                                              defaultBindingMode: BindingMode.TwoWay);

    // --- Read-only capability flags ---

    /// <summary>Defines the read-only <see cref="HasCamera" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasCameraProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasCamera), static o => o.HasCamera);

    /// <summary>Defines the read-only <see cref="HasExposureCompensation" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasExposureCompensationProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasExposureCompensation),
                                                                  static o => o.HasExposureCompensation);

    /// <summary>Defines the read-only <see cref="HasExposureModes" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasExposureModesProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasExposureModes), static o => o.HasExposureModes);

    /// <summary>Defines the read-only <see cref="HasFocusModes" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasFocusModesProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasFocusModes), static o => o.HasFocusModes);

    /// <summary>Defines the read-only <see cref="HasIso" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasIsoProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasIso), static o => o.HasIso);

    /// <summary>Defines the read-only <see cref="HasLight" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasLightProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasLight), static o => o.HasLight);

    /// <summary>Defines the read-only <see cref="HasManualExposure" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasManualExposureProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasManualExposure), static o => o.HasManualExposure);

    /// <summary>Defines the read-only <see cref="HasManualFocus" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasManualFocusProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasManualFocus), static o => o.HasManualFocus);

    /// <summary>Defines the read-only <see cref="HasVariableLight" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasVariableLightProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasVariableLight), static o => o.HasVariableLight);

    /// <summary>Defines the read-only <see cref="HasZoom" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasZoomProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasZoom), static o => o.HasZoom);

    /// <summary>Defines the read-only <see cref="IsExposureManual" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> IsExposureManualProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(IsExposureManual), static o => o.IsExposureManual);

    /// <summary>Defines the read-only <see cref="IsFocusManual" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> IsFocusManualProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(IsFocusManual), static o => o.IsFocusManual);

    /// <summary>Defines the <see cref="LightEnabled" /> property.</summary>
    public static readonly StyledProperty<bool> LightEnabledProperty =
        AvaloniaProperty.Register<CameraControlsView, bool>(nameof(LightEnabled),
                                                            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines the <see cref="LightLevel" /> property.</summary>
    public static readonly StyledProperty<double> LightLevelProperty =
        AvaloniaProperty.Register<CameraControlsView, double>(nameof(LightLevel),
                                                              1.0,
                                                              defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines the <see cref="ManualExposureMilliseconds" /> property.</summary>
    public static readonly StyledProperty<decimal> ManualExposureMillisecondsProperty =
        AvaloniaProperty.Register<CameraControlsView, decimal>(nameof(ManualExposureMilliseconds),
                                                               10m,
                                                               defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines the <see cref="ManualIso" /> property.</summary>
    public static readonly StyledProperty<decimal> ManualIsoProperty =
        AvaloniaProperty.Register<CameraControlsView, decimal>(nameof(ManualIso),
                                                               100m,
                                                               defaultBindingMode: BindingMode.TwoWay);

    // --- Editable control values ---

    /// <summary>Defines the <see cref="SelectedExposureMode" /> property.</summary>
    public static readonly StyledProperty<ExposureMode?> SelectedExposureModeProperty =
        AvaloniaProperty.Register<CameraControlsView, ExposureMode?>(nameof(SelectedExposureMode),
                                                                     defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines the <see cref="SelectedFocusMode" /> property.</summary>
    public static readonly StyledProperty<FocusMode?> SelectedFocusModeProperty =
        AvaloniaProperty.Register<CameraControlsView, FocusMode?>(nameof(SelectedFocusMode),
                                                                  defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines the <see cref="StatusChanged" /> event.</summary>
    public static readonly RoutedEvent<CameraControlsMessageEventArgs> StatusChangedEvent =
        RoutedEvent.Register<CameraControlsView, CameraControlsMessageEventArgs>(nameof(StatusChanged),
                                                                                 RoutingStrategies.Bubble);

    /// <summary>Defines the <see cref="ZoomFactor" /> property.</summary>
    public static readonly StyledProperty<double> ZoomFactorProperty =
        AvaloniaProperty.Register<CameraControlsView, double>(nameof(ZoomFactor),
                                                              1.0,
                                                              defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines the read-only <see cref="ZoomMaximum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> ZoomMaximumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(ZoomMaximum), static o => o.ZoomMaximum);

    /// <summary>Defines the read-only <see cref="ZoomMinimum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> ZoomMinimumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(ZoomMinimum), static o => o.ZoomMinimum);

    /// <summary>Defines the read-only <see cref="ZoomStep" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> ZoomStepProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(ZoomStep), static o => o.ZoomStep);

    /// <summary>Defines the read-only <see cref="ManualExposureMinimum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, decimal> ManualExposureMinimumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, decimal>(nameof(ManualExposureMinimum),
                                                                      static o => o.ManualExposureMinimum);

    /// <summary>Defines the read-only <see cref="ManualExposureMaximum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, decimal> ManualExposureMaximumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, decimal>(nameof(ManualExposureMaximum),
                                                                      static o => o.ManualExposureMaximum);

    /// <summary>Defines the read-only <see cref="ManualExposureStep" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, decimal> ManualExposureStepProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, decimal>(nameof(ManualExposureStep),
                                                                      static o => o.ManualExposureStep);

    /// <summary>Defines the read-only <see cref="IsoMinimum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, decimal> IsoMinimumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, decimal>(nameof(IsoMinimum), static o => o.IsoMinimum);

    /// <summary>Defines the read-only <see cref="IsoMaximum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, decimal> IsoMaximumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, decimal>(nameof(IsoMaximum), static o => o.IsoMaximum);

    /// <summary>Defines the read-only <see cref="IsoStep" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, decimal> IsoStepProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, decimal>(nameof(IsoStep), static o => o.IsoStep);

    /// <summary>Defines the read-only <see cref="FocusPositionMinimum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> FocusPositionMinimumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(FocusPositionMinimum),
                                                                     static o => o.FocusPositionMinimum);

    /// <summary>Defines the read-only <see cref="FocusPositionMaximum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> FocusPositionMaximumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(FocusPositionMaximum),
                                                                     static o => o.FocusPositionMaximum);

    /// <summary>Defines the read-only <see cref="FocusPositionStep" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> FocusPositionStepProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(FocusPositionStep),
                                                                     static o => o.FocusPositionStep);

    /// <summary>Defines the read-only <see cref="LightLevelMinimum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> LightLevelMinimumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(LightLevelMinimum),
                                                                     static o => o.LightLevelMinimum);

    /// <summary>Defines the read-only <see cref="LightLevelMaximum" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> LightLevelMaximumProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(LightLevelMaximum),
                                                                     static o => o.LightLevelMaximum);

    /// <summary>Defines the read-only <see cref="LightLevelStep" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, double> LightLevelStepProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, double>(nameof(LightLevelStep),
                                                                     static o => o.LightLevelStep);

    /// <summary>Defines the read-only <see cref="LastError" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, string?> LastErrorProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, string?>(nameof(LastError), static o => o.LastError);

    /// <summary>Defines the read-only <see cref="HasError" /> property.</summary>
    public static readonly DirectProperty<CameraControlsView, bool> HasErrorProperty =
        AvaloniaProperty.RegisterDirect<CameraControlsView, bool>(nameof(HasError), static o => o.HasError);

    private readonly Dictionary<string, string> _validationErrors = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _debounce;

    private CancellationTokenSource? _loadCancellation;
    private CameraCapabilities? _capabilities;
    private Func<Task>? _pendingApply;
    private string? _pendingDescription;
    private int _suppressApply;

    private IReadOnlyList<ExposureMode> _exposureModes = [];
    private IReadOnlyList<FocusMode> _focusModes = [];
    private double _exposureCompensationMinimum;
    private double _exposureCompensationMaximum;
    private double _exposureCompensationStep = 0.1;
    private double _zoomMinimum = 1;
    private double _zoomMaximum = 1;
    private double _zoomStep = 0.1;
    private decimal _manualExposureMinimum = 0.001m;
    private decimal _manualExposureMaximum = 60000m;
    private decimal _manualExposureStep = 0.1m;
    private decimal _isoMinimum = 1m;
    private decimal _isoMaximum = 100000m;
    private decimal _isoStep = 1m;
    private double _focusPositionMinimum;
    private double _focusPositionMaximum = 1;
    private double _focusPositionStep = 0.01;
    private double _lightLevelMinimum;
    private double _lightLevelMaximum = 1;
    private double _lightLevelStep = 0.01;
    private string? _lastError;
    private bool _hasError;

    private bool _hasCamera;
    private bool _hasExposureModes;
    private bool _hasExposureCompensation;
    private bool _hasManualExposure;
    private bool _hasIso;
    private bool _hasFocusModes;
    private bool _hasManualFocus;
    private bool _hasZoom;
    private bool _hasLight;
    private bool _hasVariableLight;
    private bool _isExposureManual;
    private bool _isFocusManual;

    /// <summary>Initializes a new instance of the <see cref="CameraControlsView" /> class.</summary>
    public CameraControlsView()
    {
        _debounce = new DispatcherTimer(ApplyDelay, DispatcherPriority.Background, OnDebounceTick);
        _debounce.Stop();
    }

    /// <summary>
    /// Gets or sets how long continuous edits are coalesced before being applied to the camera.
    /// </summary>
    public TimeSpan ApplyDelay
    {
        get => GetValue(ApplyDelayProperty);
        set => SetValue(ApplyDelayProperty, value);
    }

    /// <summary>Gets or sets the camera whose controls are presented.</summary>
    public Camera? Camera
    {
        get => GetValue(CameraProperty);
        set => SetValue(CameraProperty, value);
    }

    /// <summary>Gets or sets the exposure compensation value in EV.</summary>
    public double ExposureCompensation
    {
        get => GetValue(ExposureCompensationProperty);
        set => SetValue(ExposureCompensationProperty, value);
    }

    /// <summary>Gets the maximum exposure compensation value in EV.</summary>
    public double ExposureCompensationMaximum
    {
        get => _exposureCompensationMaximum;
        private set => SetAndRaise(ExposureCompensationMaximumProperty, ref _exposureCompensationMaximum, value);
    }

    /// <summary>Gets the minimum exposure compensation value in EV.</summary>
    public double ExposureCompensationMinimum
    {
        get => _exposureCompensationMinimum;
        private set => SetAndRaise(ExposureCompensationMinimumProperty, ref _exposureCompensationMinimum, value);
    }

    /// <summary>Gets the step between selectable exposure compensation values.</summary>
    public double ExposureCompensationStep
    {
        get => _exposureCompensationStep;
        private set => SetAndRaise(ExposureCompensationStepProperty, ref _exposureCompensationStep, value);
    }

    /// <summary>Gets the exposure modes reported by the camera.</summary>
    public IReadOnlyList<ExposureMode> ExposureModes
    {
        get => _exposureModes;
        private set => SetAndRaise(ExposureModesProperty, ref _exposureModes, value);
    }

    /// <summary>Gets the focus modes reported by the camera.</summary>
    public IReadOnlyList<FocusMode> FocusModes
    {
        get => _focusModes;
        private set => SetAndRaise(FocusModesProperty, ref _focusModes, value);
    }

    /// <summary>Gets or sets the manual focus position, in the range 0 to 1.</summary>
    public double FocusPosition
    {
        get => GetValue(FocusPositionProperty);
        set => SetValue(FocusPositionProperty, value);
    }

    /// <summary>Gets whether a camera is attached. Mirrored by the <c>:has-camera</c> pseudo-class.</summary>
    public bool HasCamera
    {
        get => _hasCamera;
        private set => SetFlag(HasCameraProperty, ref _hasCamera, value, HasCameraClass);
    }

    /// <summary>Gets whether exposure compensation is supported.</summary>
    public bool HasExposureCompensation
    {
        get => _hasExposureCompensation;
        private set => SetFlag(HasExposureCompensationProperty, ref _hasExposureCompensation, value, HasExposureCompensationClass);
    }

    /// <summary>Gets whether the camera exposes selectable exposure modes.</summary>
    public bool HasExposureModes
    {
        get => _hasExposureModes;
        private set => SetFlag(HasExposureModesProperty, ref _hasExposureModes, value, HasExposureModesClass);
    }

    /// <summary>Gets whether the camera exposes selectable focus modes.</summary>
    public bool HasFocusModes
    {
        get => _hasFocusModes;
        private set => SetFlag(HasFocusModesProperty, ref _hasFocusModes, value, HasFocusModesClass);
    }

    /// <summary>Gets whether manual ISO control is supported.</summary>
    public bool HasIso
    {
        get => _hasIso;
        private set => SetFlag(HasIsoProperty, ref _hasIso, value, HasIsoClass);
    }

    /// <summary>Gets whether the light is available.</summary>
    public bool HasLight
    {
        get => _hasLight;
        private set => SetFlag(HasLightProperty, ref _hasLight, value, HasLightClass);
    }

    /// <summary>Gets whether manual exposure duration is supported.</summary>
    public bool HasManualExposure
    {
        get => _hasManualExposure;
        private set => SetFlag(HasManualExposureProperty, ref _hasManualExposure, value, HasManualExposureClass);
    }

    /// <summary>Gets whether manual focus position is supported.</summary>
    public bool HasManualFocus
    {
        get => _hasManualFocus;
        private set => SetFlag(HasManualFocusProperty, ref _hasManualFocus, value, HasManualFocusClass);
    }

    /// <summary>Gets whether the light supports a variable level.</summary>
    public bool HasVariableLight
    {
        get => _hasVariableLight;
        private set => SetFlag(HasVariableLightProperty, ref _hasVariableLight, value, HasVariableLightClass);
    }

    /// <summary>Gets whether zoom control is supported.</summary>
    public bool HasZoom
    {
        get => _hasZoom;
        private set => SetFlag(HasZoomProperty, ref _hasZoom, value, HasZoomClass);
    }

    /// <summary>Gets whether the selected exposure mode is manual or locked.</summary>
    public bool IsExposureManual
    {
        get => _isExposureManual;
        private set => SetFlag(IsExposureManualProperty, ref _isExposureManual, value, ExposureManualClass);
    }

    /// <summary>Gets whether the selected focus mode is manual or locked.</summary>
    public bool IsFocusManual
    {
        get => _isFocusManual;
        private set => SetFlag(IsFocusManualProperty, ref _isFocusManual, value, FocusManualClass);
    }

    private bool IsSuppressed => Volatile.Read(ref _suppressApply) != 0;

    /// <summary>Gets or sets whether the light is enabled.</summary>
    public bool LightEnabled
    {
        get => GetValue(LightEnabledProperty);
        set => SetValue(LightEnabledProperty, value);
    }

    /// <summary>Gets or sets the light level, in the range 0 to 1.</summary>
    public double LightLevel
    {
        get => GetValue(LightLevelProperty);
        set => SetValue(LightLevelProperty, value);
    }

    /// <summary>Gets or sets the manual exposure duration in milliseconds.</summary>
    public decimal ManualExposureMilliseconds
    {
        get => GetValue(ManualExposureMillisecondsProperty);
        set => SetValue(ManualExposureMillisecondsProperty, value);
    }

    /// <summary>Gets or sets the manual ISO value.</summary>
    public decimal ManualIso
    {
        get => GetValue(ManualIsoProperty);
        set => SetValue(ManualIsoProperty, value);
    }

    /// <summary>Gets or sets the currently selected exposure mode.</summary>
    public ExposureMode? SelectedExposureMode
    {
        get => GetValue(SelectedExposureModeProperty);
        set => SetValue(SelectedExposureModeProperty, value);
    }

    /// <summary>Gets or sets the currently selected focus mode.</summary>
    public FocusMode? SelectedFocusMode
    {
        get => GetValue(SelectedFocusModeProperty);
        set => SetValue(SelectedFocusModeProperty, value);
    }

    /// <summary>Gets or sets the current zoom factor.</summary>
    public double ZoomFactor
    {
        get => GetValue(ZoomFactorProperty);
        set => SetValue(ZoomFactorProperty, value);
    }

    /// <summary>Gets the maximum supported zoom factor.</summary>
    public double ZoomMaximum
    {
        get => _zoomMaximum;
        private set => SetAndRaise(ZoomMaximumProperty, ref _zoomMaximum, value);
    }

    /// <summary>Gets the minimum supported zoom factor.</summary>
    public double ZoomMinimum
    {
        get => _zoomMinimum;
        private set => SetAndRaise(ZoomMinimumProperty, ref _zoomMinimum, value);
    }

    /// <summary>Gets the step between selectable zoom factors.</summary>
    public double ZoomStep
    {
        get => _zoomStep;
        private set => SetAndRaise(ZoomStepProperty, ref _zoomStep, value);
    }

    /// <summary>Gets the shortest manual exposure duration in milliseconds.</summary>
    public decimal ManualExposureMinimum
    {
        get => _manualExposureMinimum;
        private set => SetAndRaise(ManualExposureMinimumProperty, ref _manualExposureMinimum, value);
    }

    /// <summary>Gets the longest manual exposure duration in milliseconds.</summary>
    public decimal ManualExposureMaximum
    {
        get => _manualExposureMaximum;
        private set => SetAndRaise(ManualExposureMaximumProperty, ref _manualExposureMaximum, value);
    }

    /// <summary>Gets the step between selectable manual exposure durations in milliseconds.</summary>
    public decimal ManualExposureStep
    {
        get => _manualExposureStep;
        private set => SetAndRaise(ManualExposureStepProperty, ref _manualExposureStep, value);
    }

    /// <summary>Gets the minimum supported ISO value.</summary>
    public decimal IsoMinimum
    {
        get => _isoMinimum;
        private set => SetAndRaise(IsoMinimumProperty, ref _isoMinimum, value);
    }

    /// <summary>Gets the maximum supported ISO value.</summary>
    public decimal IsoMaximum
    {
        get => _isoMaximum;
        private set => SetAndRaise(IsoMaximumProperty, ref _isoMaximum, value);
    }

    /// <summary>Gets the step between selectable ISO values.</summary>
    public decimal IsoStep
    {
        get => _isoStep;
        private set => SetAndRaise(IsoStepProperty, ref _isoStep, value);
    }

    /// <summary>Gets the minimum supported manual focus position.</summary>
    public double FocusPositionMinimum
    {
        get => _focusPositionMinimum;
        private set => SetAndRaise(FocusPositionMinimumProperty, ref _focusPositionMinimum, value);
    }

    /// <summary>Gets the maximum supported manual focus position.</summary>
    public double FocusPositionMaximum
    {
        get => _focusPositionMaximum;
        private set => SetAndRaise(FocusPositionMaximumProperty, ref _focusPositionMaximum, value);
    }

    /// <summary>Gets the step between selectable manual focus positions.</summary>
    public double FocusPositionStep
    {
        get => _focusPositionStep;
        private set => SetAndRaise(FocusPositionStepProperty, ref _focusPositionStep, value);
    }

    /// <summary>Gets the minimum supported light level.</summary>
    public double LightLevelMinimum
    {
        get => _lightLevelMinimum;
        private set => SetAndRaise(LightLevelMinimumProperty, ref _lightLevelMinimum, value);
    }

    /// <summary>Gets the maximum supported light level.</summary>
    public double LightLevelMaximum
    {
        get => _lightLevelMaximum;
        private set => SetAndRaise(LightLevelMaximumProperty, ref _lightLevelMaximum, value);
    }

    /// <summary>Gets the step between selectable light levels.</summary>
    public double LightLevelStep
    {
        get => _lightLevelStep;
        private set => SetAndRaise(LightLevelStepProperty, ref _lightLevelStep, value);
    }

    /// <summary>
    /// Gets the message of the most recent failed control operation, or <see langword="null" />
    /// when the last operation succeeded.
    /// </summary>
    public string? LastError
    {
        get => _lastError;
        private set => SetAndRaise(LastErrorProperty, ref _lastError, value);
    }

    /// <summary>
    /// Gets whether the most recent control operation failed. Mirrored by the
    /// <c>:has-error</c> pseudo-class.
    /// </summary>
    public bool HasError
    {
        get => _hasError;
        private set => SetFlag(HasErrorProperty, ref _hasError, value, HasErrorClass);
    }

    /// <summary>Raised when a control operation fails.</summary>
    public event EventHandler<CameraControlsMessageEventArgs>? ErrorOccurred
    {
        add => AddHandler(ErrorOccurredEvent, value);
        remove => RemoveHandler(ErrorOccurredEvent, value);
    }

    /// <summary>Raised when a control operation is applied successfully.</summary>
    public event EventHandler<CameraControlsMessageEventArgs>? StatusChanged
    {
        add => AddHandler(StatusChangedEvent, value);
        remove => RemoveHandler(StatusChangedEvent, value);
    }

    /// <summary>
    /// Reloads capabilities and current control values from the attached camera. Does nothing
    /// when no camera is attached.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var camera = Camera;
        if (camera is null)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var capabilities = await camera.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(true);
            _capabilities = capabilities;
            ApplyCapabilities(capabilities);
            await ReadStateAsync(camera, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-reads only the current control values from the attached camera, leaving the cached
    /// capabilities untouched.
    /// </summary>
    public async Task RefreshStateAsync(CancellationToken cancellationToken = default)
    {
        var camera = Camera;
        if (camera is null)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await ReadStateAsync(camera, cancellationToken).ConfigureAwait(true);
            RaiseStatus("Control state refreshed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CameraProperty)
        {
            OnCameraChanged(change.GetNewValue<Camera?>());
            return;
        }

        if (change.Property == ApplyDelayProperty)
        {
            _debounce.Interval = ApplyDelay;
            return;
        }

        if (change.Property == SelectedExposureModeProperty)
        {
            var mode = change.GetNewValue<ExposureMode?>();
            IsExposureManual = mode is ExposureMode.Manual or ExposureMode.Locked;
            if (!IsSuppressed && mode is { } value)
                _ = ApplyNowAsync(camera => camera.Controls.Exposure.SetModeAsync(value), $"Exposure mode: {value}");
            return;
        }

        if (change.Property == SelectedFocusModeProperty)
        {
            var mode = change.GetNewValue<FocusMode?>();
            IsFocusManual = mode is FocusMode.Manual or FocusMode.Locked;
            if (!IsSuppressed && mode is { } value)
                _ = ApplyNowAsync(camera => camera.Controls.Focus.SetModeAsync(value), $"Focus mode: {value}");
            return;
        }

        if (IsSuppressed)
            return;

        if (change.Property == LightEnabledProperty)
        {
            var enabled = change.GetNewValue<bool>();
            _ = ApplyNowAsync(camera => camera.Controls.Light.SetEnabledAsync(enabled),
                              $"Light: {(enabled ? "on" : "off")}");
        }
        else if (change.Property == ExposureCompensationProperty)
        {
            var value = change.GetNewValue<double>();
            ScheduleApply(camera => camera.Controls.Exposure.SetCompensationAsync(value),
                          $"Exposure compensation: {value:+0.###;-0.###;0} EV");
        }
        else if (change.Property == ManualExposureMillisecondsProperty ||
                 change.Property == ManualIsoProperty)
        {
            // Validate before touching the camera so an out-of-range entry is reported
            // immediately and precisely instead of after a debounce and a driver round trip.
            if (!ValidateManualExposure())
                return;

            var duration = TimeSpan.FromMilliseconds((double) ManualExposureMilliseconds);
            var iso = HasIso ? (double?) ManualIso : null;
            var description = change.Property == ManualIsoProperty
                ? $"ISO: {ManualIso}"
                : $"Manual exposure: {ManualExposureMilliseconds:0.###} ms";
            ScheduleApply(camera => camera.Controls.Exposure.SetManualAsync(duration, iso), description);
        }
        else if (change.Property == FocusPositionProperty)
        {
            var value = change.GetNewValue<double>();
            ScheduleApply(camera => camera.Controls.Focus.SetPositionAsync(value),
                          $"Focus position: {value:0.###}");
        }
        else if (change.Property == ZoomFactorProperty)
        {
            var value = change.GetNewValue<double>();
            ScheduleApply(camera => camera.Controls.Zoom.SetFactorAsync(value), $"Zoom: {value:0.###}×");
        }
        else if (change.Property == LightLevelProperty)
        {
            var value = change.GetNewValue<double>();
            ScheduleApply(camera => camera.Controls.Light.SetLevelAsync(value), $"Light level: {value:0.###}");
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Drop any queued edit so a detached control never touches the camera.
        _debounce.Stop();
        _pendingApply = null;
        _pendingDescription = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnCameraChanged(Camera? camera)
    {
        _debounce.Stop();
        _pendingApply = null;
        _pendingDescription = null;

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;

        _capabilities = null;
        ClearValidationErrors();
        ClearError();
        HasCamera = camera is not null;

        if (camera is null)
        {
            ResetCapabilities();
            return;
        }

        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        _ = LoadAsync(token);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReloadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The camera was replaced or removed while loading.
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
    }

    private void ResetCapabilities()
    {
        ExposureModes = [];
        FocusModes = [];
        HasExposureModes = false;
        HasExposureCompensation = false;
        HasManualExposure = false;
        HasIso = false;
        HasFocusModes = false;
        HasManualFocus = false;
        HasZoom = false;
        HasLight = false;
        HasVariableLight = false;

        using (Suppress())
        {
            SelectedExposureMode = null;
            SelectedFocusMode = null;
        }
    }

    private void ApplyCapabilities(CameraCapabilities capabilities)
    {
        using var _ = Suppress();

        ExposureModes = [.. capabilities.Exposure.Modes.OrderBy(static m => m)];
        HasExposureModes = ExposureModes.Count != 0;

        // Deliberately no guess here. Picking the first mode would present enum declaration
        // order as if it were the camera's state. ReadStateAsync reflects what the camera
        // actually reports, and an unreported mode stays empty.
        SelectedExposureMode = null;

        var compensation = capabilities.Exposure.CompensationEv;
        HasExposureCompensation = compensation is not null;
        if (compensation is not null)
        {
            ExposureCompensationMinimum = compensation.Minimum;
            ExposureCompensationMaximum = compensation.Maximum;
            ExposureCompensationStep = compensation.Step > 0 ? compensation.Step : 0.1;
            ExposureCompensation = compensation.Default;
        }

        var duration = capabilities.Exposure.Duration;
        HasManualExposure = duration is not null;
        if (duration is not null)
        {
            // Drive the editor bounds from the camera so out-of-range values cannot be entered.
            ManualExposureMinimum = (decimal) duration.Minimum.TotalMilliseconds;
            ManualExposureMaximum = (decimal) duration.Maximum.TotalMilliseconds;
            var durationStep = (decimal) duration.Step.TotalMilliseconds;
            ManualExposureStep = durationStep > 0 ? durationStep : 0.1m;
            ManualExposureMilliseconds = (decimal) duration.Default.TotalMilliseconds;
        }

        var iso = capabilities.Exposure.Iso;
        HasIso = iso is not null;
        if (iso is not null)
        {
            IsoMinimum = (decimal) iso.Minimum;
            IsoMaximum = (decimal) iso.Maximum;
            IsoStep = iso.Step > 0 ? (decimal) iso.Step : 1m;
            ManualIso = (decimal) iso.Default;
        }

        FocusModes = [.. capabilities.Focus.Modes.OrderBy(static m => m)];
        HasFocusModes = FocusModes.Count != 0;
        SelectedFocusMode = null;

        var focusPosition = capabilities.Focus.ManualPosition;
        HasManualFocus = focusPosition is not null;
        if (focusPosition is not null)
        {
            FocusPositionMinimum = focusPosition.Minimum;
            FocusPositionMaximum = focusPosition.Maximum;
            FocusPositionStep = focusPosition.Step > 0 ? focusPosition.Step : 0.01;
        }

        FocusPosition = focusPosition?.Default ?? 0.5;

        var zoom = capabilities.Zoom.Factor;
        HasZoom = zoom is not null;
        if (zoom is not null)
        {
            ZoomMinimum = zoom.Minimum;
            ZoomMaximum = zoom.Maximum;
            ZoomStep = zoom.Step > 0 ? zoom.Step : 0.1;
            ZoomFactor = zoom.Default;
        }

        HasLight = capabilities.Light.IsAvailable;
        HasVariableLight = capabilities.Light.SupportsVariableLevel;

        var level = capabilities.Light.Level;
        if (level is not null)
        {
            LightLevelMinimum = level.Minimum;
            LightLevelMaximum = level.Maximum;
            LightLevelStep = level.Step > 0 ? level.Step : 0.01;
        }

        LightLevel = level?.Default ?? 1;
    }

    private async Task ReadStateAsync(Camera camera, CancellationToken cancellationToken)
    {
        if (_capabilities?.Exposure.IsSupported == true)
        {
            var exposure = await camera.Controls.Exposure.GetStateAsync(cancellationToken).ConfigureAwait(true);
            using var _ = Suppress();

            // Reflect the camera exactly: an unreported mode leaves the picker empty rather
            // than showing a stale or invented selection.
            SelectedExposureMode = exposure.Mode;
            ExposureCompensation = exposure.CompensationEv ?? ExposureCompensation;
            if (exposure.Duration is { } duration)
                ManualExposureMilliseconds = (decimal) duration.TotalMilliseconds;
            if (exposure.Iso is { } iso)
                ManualIso = (decimal) iso;
        }

        if (_capabilities?.Focus.IsSupported == true)
        {
            var focus = await camera.Controls.Focus.GetStateAsync(cancellationToken).ConfigureAwait(true);
            using var _ = Suppress();
            SelectedFocusMode = focus.Mode;
            FocusPosition = focus.Position ?? FocusPosition;
        }

        if (_capabilities?.Zoom.IsSupported == true)
        {
            var factor = await camera.Controls.Zoom.GetFactorAsync(cancellationToken).ConfigureAwait(true);
            using var _ = Suppress();
            ZoomFactor = factor;
        }

        if (_capabilities?.Light.IsAvailable == true)
        {
            var light = await camera.Controls.Light.GetStateAsync(cancellationToken).ConfigureAwait(true);
            using var _ = Suppress();
            LightEnabled = light.IsEnabled ?? LightEnabled;
            LightLevel = light.Level ?? LightLevel;
        }
    }

    // --- Applying edits ---

    private void ScheduleApply(Func<Camera, ValueTask> apply, string description)
    {
        if (Camera is null)
            return;

        _pendingApply = () =>
        {
            var camera = Camera;
            return camera is null ? Task.CompletedTask : apply(camera).AsTask();
        };
        _pendingDescription = description;
        _debounce.Stop();
        _debounce.Start();
    }

    private async void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();

        var apply = _pendingApply;
        var description = _pendingDescription;
        _pendingApply = null;
        _pendingDescription = null;

        if (apply is null)
            return;

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            await apply().ConfigureAwait(true);
            if (description is not null)
                RaiseStatus(description);
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyNowAsync(Func<Camera, ValueTask> apply, string description)
    {
        var camera = Camera;
        if (camera is null)
            return;

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            await apply(camera).ConfigureAwait(true);
            RaiseStatus(description);
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RaiseStatus(string message)
    {
        ClearError();
        RaiseEvent(new CameraControlsMessageEventArgs(StatusChangedEvent, message));
    }

    private void RaiseError(Exception exception)
    {
        var message = exception is CameraException cameraException
            ? cameraException.GetUserMessage()
            : exception.Message;

        LastError = message;
        HasError = true;

        RaiseEvent(new CameraControlsMessageEventArgs(ErrorOccurredEvent, message, exception));

        // The camera rejected the value, so the editor is now showing something the hardware
        // never accepted. Pull the real state back so the UI stops lying about it.
        _ = ResyncAfterFailureAsync();
    }

    private void ClearError()
    {
        LastError = null;
        HasError = _validationErrors.Count != 0;
        if (HasError)
            LastError = _validationErrors.Values.First();
    }

    // --- Input validation ---

    /// <summary>
    /// Validates the manual exposure editors against the ranges the camera reported. Returns
    /// <see langword="true" /> when the values may be sent to the camera.
    /// </summary>
    private bool ValidateManualExposure()
    {
        var durationValid = !HasManualExposure ||
                            IsInRange(ManualExposureMilliseconds, ManualExposureMinimum, ManualExposureMaximum);
        SetValidationError(nameof(ManualExposureMilliseconds),
                           durationValid
                               ? null
                               : $"Exposure duration must be between {ManualExposureMinimum:0.###} and {ManualExposureMaximum:0.###} ms.");

        var isoValid = !HasIso || IsInRange(ManualIso, IsoMinimum, IsoMaximum);
        SetValidationError(nameof(ManualIso),
                           isoValid ? null : $"ISO must be between {IsoMinimum:0} and {IsoMaximum:0}.");

        return durationValid && isoValid;
    }

    private static bool IsInRange(decimal value, decimal minimum, decimal maximum) =>
        value >= minimum && value <= maximum;

    private void SetValidationError(string propertyName, string? message)
    {
        var had = _validationErrors.TryGetValue(propertyName, out var existing);

        if (message is null)
        {
            if (!had)
                return;
            _validationErrors.Remove(propertyName);
        }
        else
        {
            if (had && existing == message)
                return;
            _validationErrors[propertyName] = message;
        }

        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));

        // Surface the newest validation problem in the banner too, and drop it once fixed.
        if (_validationErrors.Count != 0)
        {
            LastError = message ?? _validationErrors.Values.First();
            HasError = true;
        }
        else
        {
            LastError = null;
            HasError = false;
        }
    }

    private void ClearValidationErrors()
    {
        if (_validationErrors.Count == 0)
            return;

        var names = _validationErrors.Keys.ToArray();
        _validationErrors.Clear();
        foreach (var name in names)
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(name));
    }

    /// <inheritdoc />
    bool INotifyDataErrorInfo.HasErrors => _validationErrors.Count != 0;

    /// <inheritdoc />
    event EventHandler<DataErrorsChangedEventArgs>? INotifyDataErrorInfo.ErrorsChanged
    {
        add => ErrorsChanged += value;
        remove => ErrorsChanged -= value;
    }

    /// <inheritdoc />
    IEnumerable INotifyDataErrorInfo.GetErrors(string? propertyName) =>
        propertyName is not null && _validationErrors.TryGetValue(propertyName, out var message)
            ? new[] { message }
            : Array.Empty<string>();

    private event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    private async Task ResyncAfterFailureAsync()
    {
        var camera = Camera;
        if (camera is null || _capabilities is null)
            return;

        // RaiseError runs while the caller still holds the gate, so this must wait for it.
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            await ReadStateAsync(camera, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Reporting a resync failure would recurse; the original error is already surfaced.
            System.Diagnostics.Trace.TraceError($"Could not resync camera control state: {exception}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // --- Helpers ---

    private void SetFlag(DirectPropertyBase<bool> property, ref bool field, bool value, string pseudoClass)
    {
        if (SetAndRaise(property, ref field, value))
            SetPseudoClass(pseudoClass, value);
    }

    private void SetPseudoClass(string pseudoClass, bool isActive)
    {
        if (isActive)
            PseudoClasses.Add(pseudoClass);
        else
            PseudoClasses.Remove(pseudoClass);
    }

    private SuppressScope Suppress() => new(this);

    #region Nested: SuppressScope

    private readonly struct SuppressScope : IDisposable
    {
        private readonly CameraControlsView _owner;

        public SuppressScope(CameraControlsView owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._suppressApply);
        }

        public void Dispose() => Interlocked.Decrement(ref _owner._suppressApply);
    }

    #endregion
}
