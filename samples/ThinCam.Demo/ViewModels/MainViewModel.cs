using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using SkiaSharp;
using ThinCam.Demo.Infrastructure;
using ThinCam.Demo.Services;
using ThinCam.Avalonia;
using ThinCam.SkiaSharp;

namespace ThinCam.Demo.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPlatformServices _platformServices;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly DispatcherTimer _statsTimer;
    private readonly List<string> _logLines = [];
    private Camera? _camera;
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private CameraDeviceItem? _selectedDevice;
    private CameraCapabilities? _capabilities;
    private CameraPermissionStatus _permissionStatus;
    private string _statusText = "Ready";
    private string _activeFormatText = "No active format";
    private string _lastError = "None";
    private string _diagnosticText = string.Empty;
    private decimal _requestedWidth = 1280;
    private decimal _requestedHeight = 720;
    private decimal _requestedFramesPerSecond = 30;
    private decimal _queueCapacity = 2;
    private ExposureMode? _selectedExposureMode;
    private FocusMode? _selectedFocusMode;
    private double _exposureCompensation;
    private double _exposureCompensationMinimum;
    private double _exposureCompensationMaximum;
    private double _exposureCompensationStep = 0.1;
    private decimal _manualExposureMilliseconds = 10;
    private decimal _manualIso = 100;
    private double _focusPosition = 0.5;
    private double _zoomFactor = 1;
    private double _zoomMinimum = 1;
    private double _zoomMaximum = 1;
    private double _zoomStep = 0.1;
    private bool _lightEnabled;
    private double _lightLevel = 1;
    private bool _hasExposureModes;
    private bool _hasExposureCompensation;
    private bool _hasManualExposure;
    private bool _hasIso;
    private bool _hasFocusModes;
    private bool _hasManualFocus;
    private bool _hasZoom;
    private bool _hasLight;
    private bool _hasVariableLight;
    private double _receivedFramesPerSecond;
    private double _renderedFramesPerSecond;
    private long _totalFrames;
    private long _renderedFrames;
    private long _previewFramesSkipped;
    private string _lastFrameAge = "—";
    private long _receivedCounter;
    private long _renderedCounter;
    private long _previousReceivedCounter;
    private long _previousRenderedCounter;
    private long _lastStatsTimestamp;
    private long _lastFrameTimestamp;
    private int _disposed;

    public MainViewModel(IPlatformServices platformServices)
    {
        _platformServices = platformServices ?? throw new ArgumentNullException(nameof(platformServices));
        PreviewSource = new CameraPreviewSource();

        RequestPermissionCommand = new AsyncCommand(RequestPermissionAsync);
        RefreshDevicesCommand = new AsyncCommand(RefreshDevicesAsync);
        OpenCameraCommand = new AsyncCommand(OpenCameraAsync, () => SelectedDevice is not null && !IsCameraOpen);
        CloseCameraCommand = new AsyncCommand(CloseCameraAsync, () => IsCameraOpen);
        RestartCameraCommand = new AsyncCommand(RestartCameraAsync, () => SelectedDevice is not null);
        RefreshCapabilitiesCommand = new AsyncCommand(RefreshCapabilitiesAsync, () => IsCameraOpen);
        RefreshControlStateCommand = new AsyncCommand(RefreshControlStateAsync, () => IsCameraOpen);
        ApplyExposureModeCommand = new AsyncCommand(ApplyExposureModeAsync, () => IsCameraOpen && HasExposureModes);
        ApplyExposureCompensationCommand = new AsyncCommand(ApplyExposureCompensationAsync, () => IsCameraOpen && HasExposureCompensation);
        ApplyManualExposureCommand = new AsyncCommand(ApplyManualExposureAsync, () => IsCameraOpen && HasManualExposure);
        ApplyFocusModeCommand = new AsyncCommand(ApplyFocusModeAsync, () => IsCameraOpen && HasFocusModes);
        ApplyFocusPositionCommand = new AsyncCommand(ApplyFocusPositionAsync, () => IsCameraOpen && HasManualFocus);
        ApplyZoomCommand = new AsyncCommand(ApplyZoomAsync, () => IsCameraOpen && HasZoom);
        ApplyLightCommand = new AsyncCommand(ApplyLightAsync, () => IsCameraOpen && HasLight);
        SaveSnapshotCommand = new AsyncCommand(SaveSnapshotAsync, () => PreviewSource.HasFrame && SaveFileHandler is not null);
        ExportDiagnosticsCommand = new AsyncCommand(ExportDiagnosticsAsync, () => SaveFileHandler is not null);
        ClearLogCommand = new RelayCommand(ClearLog);

        _permissionStatus = SafeGetPermissionStatus();
        _lastStatsTimestamp = Stopwatch.GetTimestamp();
        _statsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, OnStatsTick);
        _statsTimer.Start();

        Log($"ThinCam Avalonia demo started on {_platformServices.PlatformDescription}.");
        Log($"Initial permission status: {_permissionStatus}.");
    }

    private Func<SaveFileRequest, CancellationToken, Task<bool>>? _saveFileHandler;

    public Func<SaveFileRequest, CancellationToken, Task<bool>>? SaveFileHandler
    {
        get => _saveFileHandler;
        set
        {
            _saveFileHandler = value;
            SaveSnapshotCommand.RaiseCanExecuteChanged();
            ExportDiagnosticsCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<CameraDeviceItem> Devices { get; } = [];
    public ObservableCollection<ExposureMode> ExposureModes { get; } = [];
    public ObservableCollection<FocusMode> FocusModes { get; } = [];
    public CameraPreviewSource PreviewSource { get; }

    public AsyncCommand RequestPermissionCommand { get; }
    public AsyncCommand RefreshDevicesCommand { get; }
    public AsyncCommand OpenCameraCommand { get; }
    public AsyncCommand CloseCameraCommand { get; }
    public AsyncCommand RestartCameraCommand { get; }
    public AsyncCommand RefreshCapabilitiesCommand { get; }
    public AsyncCommand RefreshControlStateCommand { get; }
    public AsyncCommand ApplyExposureModeCommand { get; }
    public AsyncCommand ApplyExposureCompensationCommand { get; }
    public AsyncCommand ApplyManualExposureCommand { get; }
    public AsyncCommand ApplyFocusModeCommand { get; }
    public AsyncCommand ApplyFocusPositionCommand { get; }
    public AsyncCommand ApplyZoomCommand { get; }
    public AsyncCommand ApplyLightCommand { get; }
    public AsyncCommand SaveSnapshotCommand { get; }
    public AsyncCommand ExportDiagnosticsCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public string PlatformDescription => _platformServices.PlatformDescription;
    public bool IsCameraOpen => _camera is not null;
    public string PermissionStatusText => PermissionStatus.ToString();

    public CameraPermissionStatus PermissionStatus
    {
        get => _permissionStatus;
        private set
        {
            if (SetProperty(ref _permissionStatus, value))
            {
                OnPropertyChanged(nameof(PermissionStatusText));
            }
        }
    }

    public CameraDeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ActiveFormatText
    {
        get => _activeFormatText;
        private set => SetProperty(ref _activeFormatText, value);
    }

    public string LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public string DiagnosticText
    {
        get => _diagnosticText;
        private set => SetProperty(ref _diagnosticText, value);
    }

    public decimal RequestedWidth
    {
        get => _requestedWidth;
        set => SetProperty(ref _requestedWidth, value);
    }

    public decimal RequestedHeight
    {
        get => _requestedHeight;
        set => SetProperty(ref _requestedHeight, value);
    }

    public decimal RequestedFramesPerSecond
    {
        get => _requestedFramesPerSecond;
        set => SetProperty(ref _requestedFramesPerSecond, value);
    }

    public decimal QueueCapacity
    {
        get => _queueCapacity;
        set => SetProperty(ref _queueCapacity, value);
    }

    public ExposureMode? SelectedExposureMode
    {
        get => _selectedExposureMode;
        set => SetProperty(ref _selectedExposureMode, value);
    }

    public FocusMode? SelectedFocusMode
    {
        get => _selectedFocusMode;
        set => SetProperty(ref _selectedFocusMode, value);
    }

    public double ExposureCompensation
    {
        get => _exposureCompensation;
        set => SetProperty(ref _exposureCompensation, value);
    }

    public double ExposureCompensationMinimum
    {
        get => _exposureCompensationMinimum;
        private set => SetProperty(ref _exposureCompensationMinimum, value);
    }

    public double ExposureCompensationMaximum
    {
        get => _exposureCompensationMaximum;
        private set => SetProperty(ref _exposureCompensationMaximum, value);
    }

    public double ExposureCompensationStep
    {
        get => _exposureCompensationStep;
        private set => SetProperty(ref _exposureCompensationStep, value);
    }

    public decimal ManualExposureMilliseconds
    {
        get => _manualExposureMilliseconds;
        set => SetProperty(ref _manualExposureMilliseconds, value);
    }

    public decimal ManualIso
    {
        get => _manualIso;
        set => SetProperty(ref _manualIso, value);
    }

    public double FocusPosition
    {
        get => _focusPosition;
        set => SetProperty(ref _focusPosition, value);
    }

    public double ZoomFactor
    {
        get => _zoomFactor;
        set => SetProperty(ref _zoomFactor, value);
    }

    public double ZoomMinimum
    {
        get => _zoomMinimum;
        private set => SetProperty(ref _zoomMinimum, value);
    }

    public double ZoomMaximum
    {
        get => _zoomMaximum;
        private set => SetProperty(ref _zoomMaximum, value);
    }

    public double ZoomStep
    {
        get => _zoomStep;
        private set => SetProperty(ref _zoomStep, value);
    }

    public bool LightEnabled
    {
        get => _lightEnabled;
        set => SetProperty(ref _lightEnabled, value);
    }

    public double LightLevel
    {
        get => _lightLevel;
        set => SetProperty(ref _lightLevel, value);
    }

    public bool HasExposureModes
    {
        get => _hasExposureModes;
        private set => SetCapability(ref _hasExposureModes, value);
    }

    public bool HasExposureCompensation
    {
        get => _hasExposureCompensation;
        private set => SetCapability(ref _hasExposureCompensation, value);
    }

    public bool HasManualExposure
    {
        get => _hasManualExposure;
        private set => SetCapability(ref _hasManualExposure, value);
    }

    public bool HasIso
    {
        get => _hasIso;
        private set => SetProperty(ref _hasIso, value);
    }

    public bool HasFocusModes
    {
        get => _hasFocusModes;
        private set => SetCapability(ref _hasFocusModes, value);
    }

    public bool HasManualFocus
    {
        get => _hasManualFocus;
        private set => SetCapability(ref _hasManualFocus, value);
    }

    public bool HasZoom
    {
        get => _hasZoom;
        private set => SetCapability(ref _hasZoom, value);
    }

    public bool HasLight
    {
        get => _hasLight;
        private set => SetCapability(ref _hasLight, value);
    }

    public bool HasVariableLight
    {
        get => _hasVariableLight;
        private set => SetProperty(ref _hasVariableLight, value);
    }

    public double ReceivedFramesPerSecond
    {
        get => _receivedFramesPerSecond;
        private set => SetProperty(ref _receivedFramesPerSecond, value);
    }

    public double RenderedFramesPerSecond
    {
        get => _renderedFramesPerSecond;
        private set => SetProperty(ref _renderedFramesPerSecond, value);
    }

    public long TotalFrames
    {
        get => _totalFrames;
        private set => SetProperty(ref _totalFrames, value);
    }

    public long RenderedFrames
    {
        get => _renderedFrames;
        private set => SetProperty(ref _renderedFrames, value);
    }

    public long PreviewFramesSkipped
    {
        get => _previewFramesSkipped;
        private set => SetProperty(ref _previewFramesSkipped, value);
    }

    public string LastFrameAge
    {
        get => _lastFrameAge;
        private set => SetProperty(ref _lastFrameAge, value);
    }

    public void ReportFrameRendered() => Interlocked.Increment(ref _renderedCounter);

    private async Task RequestPermissionAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusText = "Requesting camera permission…";
            PermissionStatus = await _platformServices
                .RequestCameraPermissionAsync(cancellationToken)
                .ConfigureAwait(true);
            StatusText = $"Permission: {PermissionStatus}";
            Log($"Permission request completed: {PermissionStatus}.");
        }
        catch (Exception exception)
        {
            HandleError("Permission request failed", exception);
        }
    }

    private async Task RefreshDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusText = "Enumerating cameras…";
            IReadOnlyList<CameraDevice> cameras = await Task.Run(
                () => CameraDevices.Enumerate(),
                cancellationToken).ConfigureAwait(true);

            string? previousId = SelectedDevice?.Device.Id;
            Devices.Clear();
            foreach (CameraDevice camera in cameras)
            {
                Devices.Add(new CameraDeviceItem(camera));
            }

            SelectedDevice = Devices.FirstOrDefault(item => item.Device.Id == previousId)
                ?? Devices.FirstOrDefault(item => item.Device.IsDefault)
                ?? Devices.FirstOrDefault();

            StatusText = $"Found {Devices.Count} camera(s).";
            Log($"Enumerated {Devices.Count} camera(s)." );
            foreach (CameraDeviceItem item in Devices)
            {
                Log($"Device: {item.Device.Name}; id={item.Device.Id}; position={item.Device.Position}; default={item.Device.IsDefault}.");
            }
        }
        catch (Exception exception)
        {
            HandleError("Camera enumeration failed", exception);
        }
    }

    private async Task OpenCameraAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ThrowIfDisposed();
            if (_camera is not null)
            {
                return;
            }

            if (SelectedDevice is null)
            {
                throw new InvalidOperationException("Select a camera first.");
            }

            PermissionStatus = SafeGetPermissionStatus();
            if (PermissionStatus != CameraPermissionStatus.Granted)
            {
                PermissionStatus = await _platformServices
                    .RequestCameraPermissionAsync(cancellationToken)
                    .ConfigureAwait(true);
            }

            if (PermissionStatus != CameraPermissionStatus.Granted)
            {
                throw new CameraException(
                    CameraErrorCode.PermissionDenied,
                    $"Camera permission is {PermissionStatus}.");
            }

            var options = new CameraOpenOptions
            {
                Width = ToPositiveInt(RequestedWidth, nameof(RequestedWidth)),
                Height = ToPositiveInt(RequestedHeight, nameof(RequestedHeight)),
                FramesPerSecond = ToPositiveInt(RequestedFramesPerSecond, nameof(RequestedFramesPerSecond)),
                QueueCapacity = Math.Clamp(ToPositiveInt(QueueCapacity, nameof(QueueCapacity)), 1, 32)
            };

            StatusText = $"Opening {SelectedDevice.Device.Name}…";
            LastError = "None";
            ResetFrameStatistics();

            Camera camera = await Camera.OpenAsync(
                SelectedDevice.Device,
                options,
                cancellationToken).ConfigureAwait(true);

            _camera = camera;
            _captureCancellation = new CancellationTokenSource();
            _captureTask = CaptureLoopAsync(camera, _captureCancellation.Token);
            OnPropertyChanged(nameof(IsCameraOpen));
            NotifyCommandStates();

            StatusText = $"Capturing from {camera.Device.Name}.";
            Log($"Camera opened: {camera.Device.Name}; requested={options.Width}x{options.Height}@{options.FramesPerSecond}; queue={options.QueueCapacity}.");

            await ReadCapabilitiesAsync(camera, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            HandleError("Could not open camera", exception);
            try
            {
                await StopCameraCoreAsync().ConfigureAwait(true);
            }
            catch (Exception cleanupException)
            {
                HandleError("Could not clean up the failed camera open", cleanupException);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task CloseCameraAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await StopCameraCoreAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            HandleError("Could not close camera", exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RestartCameraAsync(CancellationToken cancellationToken)
    {
        bool stopped = false;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await StopCameraCoreAsync().ConfigureAwait(true);
            stopped = true;
        }
        catch (Exception exception)
        {
            HandleError("Could not restart camera", exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (stopped)
        {
            await OpenCameraAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task StopCameraCoreAsync()
    {
        Camera? camera = _camera;
        CancellationTokenSource? cancellation = _captureCancellation;
        Task? captureTask = _captureTask;
        _camera = null;
        _captureCancellation = null;
        _captureTask = null;

        cancellation?.Cancel();
        if (camera is not null)
        {
            StatusText = "Stopping camera…";
            await camera.DisposeAsync().ConfigureAwait(true);
        }

        if (captureTask is not null)
        {
            try
            {
                await captureTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                HandleError("Capture loop stopped with an error", exception);
            }
        }

        cancellation?.Dispose();
        PreviewSource.Clear();
        ClearCapabilities();
        ActiveFormatText = "No active format";
        StatusText = "Camera stopped.";
        OnPropertyChanged(nameof(IsCameraOpen));
        NotifyCommandStates();
        Log("Camera stopped.");
    }

    private async Task CaptureLoopAsync(Camera camera, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (VideoFrame frame in camera.GetFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                using (frame)
                {
                    PreviewSource.Publish(frame, SkiaFrameTransform.Presentation);
                    Interlocked.Increment(ref _receivedCounter);
                    Interlocked.Exchange(ref _lastFrameTimestamp, Stopwatch.GetTimestamp());
                }

            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() => HandleError("Capture failed", exception));
        }
    }

    private async Task RefreshCapabilitiesAsync(CancellationToken cancellationToken)
    {
        Camera camera = GetOpenCamera();
        await ReadCapabilitiesAsync(camera, cancellationToken).ConfigureAwait(true);
    }

    private async Task ReadCapabilitiesAsync(Camera camera, CancellationToken cancellationToken)
    {
        try
        {
            StatusText = "Reading camera capabilities…";
            CameraCapabilities capabilities = await camera
                .GetCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(true);
            _capabilities = capabilities;
            ApplyCapabilities(capabilities);
            await RefreshControlStateCoreAsync(camera, cancellationToken).ConfigureAwait(true);
            StatusText = "Capabilities loaded.";
            Log(DescribeCapabilities(capabilities));
        }
        catch (Exception exception)
        {
            HandleError("Could not read camera capabilities", exception);
        }
    }

    private async Task RefreshControlStateAsync(CancellationToken cancellationToken)
    {
        await RefreshControlStateCoreAsync(GetOpenCamera(), cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshControlStateCoreAsync(Camera camera, CancellationToken cancellationToken)
    {
        try
        {
            if (_capabilities?.Exposure.IsSupported == true)
            {
                ExposureState exposure = await camera.Controls.Exposure
                    .GetStateAsync(cancellationToken).ConfigureAwait(true);
                SelectedExposureMode = exposure.Mode ?? SelectedExposureMode;
                ExposureCompensation = exposure.CompensationEv ?? ExposureCompensation;
                if (exposure.Duration is { } duration)
                {
                    ManualExposureMilliseconds = (decimal)duration.TotalMilliseconds;
                }
                if (exposure.Iso is { } iso)
                {
                    ManualIso = (decimal)iso;
                }
            }

            if (_capabilities?.Focus.IsSupported == true)
            {
                FocusState focus = await camera.Controls.Focus
                    .GetStateAsync(cancellationToken).ConfigureAwait(true);
                SelectedFocusMode = focus.Mode ?? SelectedFocusMode;
                FocusPosition = focus.Position ?? FocusPosition;
            }

            if (_capabilities?.Zoom.IsSupported == true)
            {
                ZoomFactor = await camera.Controls.Zoom
                    .GetFactorAsync(cancellationToken).ConfigureAwait(true);
            }

            if (_capabilities?.Light.IsAvailable == true)
            {
                CameraLightState light = await camera.Controls.Light
                    .GetStateAsync(cancellationToken).ConfigureAwait(true);
                LightEnabled = light.IsEnabled ?? LightEnabled;
                LightLevel = light.Level ?? LightLevel;
            }

            Log("Control state refreshed.");
        }
        catch (Exception exception)
        {
            HandleError("Could not read control state", exception);
        }
    }

    private async Task ApplyExposureModeAsync(CancellationToken cancellationToken)
    {
        if (SelectedExposureMode is not { } mode)
        {
            return;
        }

        await ExecuteControlAsync(
            $"Exposure mode {mode}",
            token => GetOpenCamera().Controls.Exposure.SetModeAsync(mode, token).AsTask(),
            cancellationToken).ConfigureAwait(true);
    }

    private Task ApplyExposureCompensationAsync(CancellationToken cancellationToken) =>
        ExecuteControlAsync(
            $"Exposure compensation {ExposureCompensation:+0.###;-0.###;0} EV",
            token => GetOpenCamera().Controls.Exposure
                .SetCompensationAsync(ExposureCompensation, token).AsTask(),
            cancellationToken);

    private Task ApplyManualExposureAsync(CancellationToken cancellationToken)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds((double)ManualExposureMilliseconds);
        double? iso = HasIso ? (double)ManualIso : null;
        return ExecuteControlAsync(
            $"Manual exposure {duration.TotalMilliseconds:0.###} ms" +
            (iso.HasValue ? $", ISO {iso.Value:0.##}" : string.Empty),
            token => GetOpenCamera().Controls.Exposure
                .SetManualAsync(duration, iso, token).AsTask(),
            cancellationToken);
    }

    private async Task ApplyFocusModeAsync(CancellationToken cancellationToken)
    {
        if (SelectedFocusMode is not { } mode)
        {
            return;
        }

        await ExecuteControlAsync(
            $"Focus mode {mode}",
            token => GetOpenCamera().Controls.Focus.SetModeAsync(mode, token).AsTask(),
            cancellationToken).ConfigureAwait(true);
    }

    private Task ApplyFocusPositionAsync(CancellationToken cancellationToken) =>
        ExecuteControlAsync(
            $"Focus position {FocusPosition:0.###}",
            token => GetOpenCamera().Controls.Focus
                .SetPositionAsync(FocusPosition, token).AsTask(),
            cancellationToken);

    private Task ApplyZoomAsync(CancellationToken cancellationToken) =>
        ExecuteControlAsync(
            $"Zoom {ZoomFactor:0.###}×",
            token => GetOpenCamera().Controls.Zoom
                .SetFactorAsync(ZoomFactor, token).AsTask(),
            cancellationToken);

    private async Task ApplyLightAsync(CancellationToken cancellationToken)
    {
        Camera camera = GetOpenCamera();
        await ExecuteControlAsync(
            $"Light {(LightEnabled ? "enabled" : "disabled")}",
            token => camera.Controls.Light.SetEnabledAsync(LightEnabled, token).AsTask(),
            cancellationToken).ConfigureAwait(true);

        if (LightEnabled && HasVariableLight)
        {
            await ExecuteControlAsync(
                $"Light level {LightLevel:0.###}",
                token => camera.Controls.Light.SetLevelAsync(LightLevel, token).AsTask(),
                cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task ExecuteControlAsync(
        string description,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            StatusText = $"Applying {description}…";
            await operation(cancellationToken).ConfigureAwait(true);
            StatusText = $"Applied {description}.";
            Log($"Applied {description}.");
        }
        catch (Exception exception)
        {
            HandleError($"Could not apply {description}", exception);
        }
    }

    private async Task SaveSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SaveFileHandler is null)
            {
                return;
            }

            using SKBitmap? snapshot = PreviewSource.CopySnapshot();
            if (snapshot is null)
            {
                StatusText = "No preview frame is available.";
                return;
            }

            using SKImage image = SKImage.FromBitmap(snapshot)
                ?? throw new InvalidOperationException("Could not create a Skia image from the preview snapshot.");
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Could not encode the preview snapshot as PNG.");

            var request = new SaveFileRequest(
                $"thincam-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png",
                "PNG image",
                "png",
                "image/png",
                data.ToArray());

            bool saved = await SaveFileHandler(request, cancellationToken).ConfigureAwait(true);
            StatusText = saved ? "Snapshot saved." : "Snapshot save cancelled.";
            if (saved)
            {
                Log($"Saved snapshot {snapshot.Width}x{snapshot.Height}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HandleError("Could not save snapshot", exception);
        }
    }

    private async Task ExportDiagnosticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SaveFileHandler is null)
            {
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(BuildDiagnosticReport());
            var request = new SaveFileRequest(
                $"thincam-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt",
                "Text report",
                "txt",
                "text/plain",
                bytes);

            bool saved = await SaveFileHandler(request, cancellationToken).ConfigureAwait(true);
            StatusText = saved ? "Diagnostic report saved." : "Diagnostic export cancelled.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HandleError("Could not export diagnostics", exception);
        }
    }

    private void ApplyCapabilities(CameraCapabilities capabilities)
    {
        ExposureModes.Clear();
        foreach (ExposureMode mode in capabilities.Exposure.Modes.OrderBy(static mode => mode))
        {
            ExposureModes.Add(mode);
        }
        HasExposureModes = ExposureModes.Count != 0;
        SelectedExposureMode = ExposureModes.FirstOrDefault();

        NumericRange<double>? compensation = capabilities.Exposure.CompensationEv;
        HasExposureCompensation = compensation is not null;
        if (compensation is not null)
        {
            ExposureCompensationMinimum = compensation.Minimum;
            ExposureCompensationMaximum = compensation.Maximum;
            ExposureCompensationStep = PositiveStep(compensation.Step, 0.1);
            ExposureCompensation = compensation.Default;
        }

        NumericRange<TimeSpan>? duration = capabilities.Exposure.Duration;
        HasManualExposure = duration is not null;
        if (duration is not null)
        {
            ManualExposureMilliseconds = (decimal)duration.Default.TotalMilliseconds;
        }

        NumericRange<double>? iso = capabilities.Exposure.Iso;
        HasIso = iso is not null;
        if (iso is not null)
        {
            ManualIso = (decimal)iso.Default;
        }

        FocusModes.Clear();
        foreach (FocusMode mode in capabilities.Focus.Modes.OrderBy(static mode => mode))
        {
            FocusModes.Add(mode);
        }
        HasFocusModes = FocusModes.Count != 0;
        SelectedFocusMode = FocusModes.FirstOrDefault();
        HasManualFocus = capabilities.Focus.ManualPosition is not null;
        FocusPosition = capabilities.Focus.ManualPosition?.Default ?? 0.5;

        NumericRange<double>? zoom = capabilities.Zoom.Factor;
        HasZoom = zoom is not null;
        if (zoom is not null)
        {
            ZoomMinimum = zoom.Minimum;
            ZoomMaximum = zoom.Maximum;
            ZoomStep = PositiveStep(zoom.Step, 0.1);
            ZoomFactor = zoom.Default;
        }

        HasLight = capabilities.Light.IsAvailable;
        HasVariableLight = capabilities.Light.SupportsVariableLevel;
        LightLevel = capabilities.Light.Level?.Default ?? 1;
        NotifyCommandStates();
    }

    private void ClearCapabilities()
    {
        _capabilities = null;
        ExposureModes.Clear();
        FocusModes.Clear();
        HasExposureModes = false;
        HasExposureCompensation = false;
        HasManualExposure = false;
        HasIso = false;
        HasFocusModes = false;
        HasManualFocus = false;
        HasZoom = false;
        HasLight = false;
        HasVariableLight = false;
    }

    private void OnStatsTick(object? sender, EventArgs eventArgs)
    {
        long now = Stopwatch.GetTimestamp();
        long received = Interlocked.Read(ref _receivedCounter);
        long rendered = Interlocked.Read(ref _renderedCounter);
        double seconds = (now - _lastStatsTimestamp) / (double)Stopwatch.Frequency;
        if (seconds > 0)
        {
            ReceivedFramesPerSecond = (received - _previousReceivedCounter) / seconds;
            RenderedFramesPerSecond = (rendered - _previousRenderedCounter) / seconds;
        }

        _lastStatsTimestamp = now;
        _previousReceivedCounter = received;
        _previousRenderedCounter = rendered;
        TotalFrames = received;
        RenderedFrames = rendered;
        PreviewFramesSkipped = Math.Max(0, received - rendered);

        long lastFrame = Interlocked.Read(ref _lastFrameTimestamp);
        LastFrameAge = lastFrame == 0
            ? "—"
            : TimeSpan.FromSeconds((now - lastFrame) / (double)Stopwatch.Frequency).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";

        CameraFormat? format = _camera?.ActiveFormat;
        ActiveFormatText = format is null
            ? (_camera is null ? "No active format" : "Waiting for first frame…")
            : $"{format.Width}×{format.Height}, stride {format.Stride}, {format.PixelFormat}";

        SaveSnapshotCommand.RaiseCanExecuteChanged();
    }

    private string BuildDiagnosticReport()
    {
        var report = new StringBuilder();
        report.AppendLine("ThinCam Avalonia diagnostic report");
        report.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTimeOffset.Now:O}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Platform: {PlatformDescription}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Permission: {PermissionStatus}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Status: {StatusText}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Camera open: {IsCameraOpen}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Selected device: {SelectedDevice?.Device.Name ?? "None"}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Device id: {SelectedDevice?.Device.Id ?? "None"}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Active format: {ActiveFormatText}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Received frames: {TotalFrames}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Rendered frames: {RenderedFrames}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Received FPS: {ReceivedFramesPerSecond:0.0}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Rendered FPS: {RenderedFramesPerSecond:0.0}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Last error: {LastError}");
        report.AppendLine();
        report.AppendLine("Capabilities:");
        report.AppendLine(_capabilities is null ? "Not loaded" : DescribeCapabilities(_capabilities));
        report.AppendLine();
        report.AppendLine("Event log:");
        report.Append(DiagnosticText);
        return report.ToString();
    }

    private static string DescribeCapabilities(CameraCapabilities capabilities)
    {
        static string Range<T>(NumericRange<T>? range) =>
            range is null ? "unsupported" : $"{range.Minimum} .. {range.Maximum}; default={range.Default}; step={range.Step}";

        return string.Join(
            Environment.NewLine,
            $"Exposure modes: {Join(capabilities.Exposure.Modes)}",
            $"Exposure compensation: {Range(capabilities.Exposure.CompensationEv)}",
            $"Exposure duration: {Range(capabilities.Exposure.Duration)}",
            $"ISO: {Range(capabilities.Exposure.Iso)}",
            $"Focus modes: {Join(capabilities.Focus.Modes)}",
            $"Focus position: {Range(capabilities.Focus.ManualPosition)}",
            $"Zoom: {Range(capabilities.Zoom.Factor)}",
            $"Light: available={capabilities.Light.IsAvailable}; variable={capabilities.Light.SupportsVariableLevel}; level={Range(capabilities.Light.Level)}");
    }

    private static string Join<T>(IEnumerable<T> values)
    {
        string joined = string.Join(", ", values);
        return joined.Length == 0 ? "unsupported" : joined;
    }

    private void HandleError(string context, Exception exception)
    {
        LastError = exception is CameraException cameraException
            ? $"{cameraException.ErrorCode}: {cameraException.Message}"
            : exception.Message;
        StatusText = context + ".";
        Log($"ERROR: {context}: {LastError}");
    }

    private void Log(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Log(message));
            return;
        }

        _logLines.Add($"{DateTimeOffset.Now:HH:mm:ss.fff}  {message}");
        if (_logLines.Count > 400)
        {
            _logLines.RemoveRange(0, _logLines.Count - 400);
        }
        DiagnosticText = string.Join(Environment.NewLine, _logLines);
    }

    private void ClearLog()
    {
        _logLines.Clear();
        DiagnosticText = string.Empty;
    }

    private bool SetCapability(ref bool field, bool value)
    {
        if (!SetProperty(ref field, value))
        {
            return false;
        }

        NotifyCommandStates();
        return true;
    }

    private void NotifyCommandStates()
    {
        OpenCameraCommand.RaiseCanExecuteChanged();
        CloseCameraCommand.RaiseCanExecuteChanged();
        RestartCameraCommand.RaiseCanExecuteChanged();
        RefreshCapabilitiesCommand.RaiseCanExecuteChanged();
        RefreshControlStateCommand.RaiseCanExecuteChanged();
        ApplyExposureModeCommand.RaiseCanExecuteChanged();
        ApplyExposureCompensationCommand.RaiseCanExecuteChanged();
        ApplyManualExposureCommand.RaiseCanExecuteChanged();
        ApplyFocusModeCommand.RaiseCanExecuteChanged();
        ApplyFocusPositionCommand.RaiseCanExecuteChanged();
        ApplyZoomCommand.RaiseCanExecuteChanged();
        ApplyLightCommand.RaiseCanExecuteChanged();
        SaveSnapshotCommand.RaiseCanExecuteChanged();
        ExportDiagnosticsCommand.RaiseCanExecuteChanged();
    }

    private static int ToPositiveInt(decimal value, string name)
    {
        if (value <= 0 || value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name);
        }
        return decimal.ToInt32(decimal.Truncate(value));
    }

    private static double PositiveStep(double? value, double fallback) =>
        value.HasValue && double.IsFinite(value.Value) && value.Value > 0
            ? value.Value
            : fallback;

    private Camera GetOpenCamera() =>
        _camera ?? throw new InvalidOperationException("Open a camera first.");

    private CameraPermissionStatus SafeGetPermissionStatus()
    {
        try
        {
            return CameraPermissions.GetStatus();
        }
        catch (Exception exception)
        {
            Log($"Could not query camera permission: {exception.Message}");
            return CameraPermissionStatus.Unknown;
        }
    }

    private void ResetFrameStatistics()
    {
        Interlocked.Exchange(ref _receivedCounter, 0);
        Interlocked.Exchange(ref _renderedCounter, 0);
        Interlocked.Exchange(ref _lastFrameTimestamp, 0);
        _previousReceivedCounter = 0;
        _previousRenderedCounter = 0;
        _lastStatsTimestamp = Stopwatch.GetTimestamp();
        ReceivedFramesPerSecond = 0;
        RenderedFramesPerSecond = 0;
        TotalFrames = 0;
        RenderedFrames = 0;
        PreviewFramesSkipped = 0;
        LastFrameAge = "—";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _statsTimer.Stop();
        await _lifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            try
            {
                await StopCameraCoreAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                HandleError("Camera shutdown failed", exception);
            }
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
            PreviewSource.Dispose();
        }
    }
}
