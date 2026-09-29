using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Threading;
using SkiaSharp;
using ThinCam;
using ThinCam.Avalonia;
using ThinCamDemo.Infrastructure;
using ThinCamDemo.Services;

namespace ThinCamDemo.ViewModels;

/// <summary>Provides camera controls, preview state, and diagnostics for the main view.</summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPlatformServices _platformServices;
    private readonly SemaphoreSlim _lifecycleGate = new SemaphoreSlim(1, 1);
    private readonly DispatcherTimer _statsTimer;

    private readonly List<string> _logLines = [];
    private readonly object _disposeSync = new object();
    private Task? _disposeTask;
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private CameraDeviceItem? _selectedDevice;
    private CameraCapabilities? _capabilities;
    private CameraPermissionStatus _permissionStatus;
    private string _statusText = "Ready";
    private string _activeFormatText = "No active format";
    private string _lastError = String.Empty;
    private string _diagnosticText = String.Empty;
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

    private Func<SaveFileRequest, CancellationToken, Task<bool>>? _saveFileHandler;

    /// <summary>Initializes a new main view model.</summary>
    /// <param name="platformServices">The host-platform services.</param>
    public MainViewModel(IPlatformServices platformServices)
    {
        _platformServices = platformServices ?? throw new ArgumentNullException(nameof(platformServices));
        PreviewSource = new CameraPreviewSource();

        RequestPermissionCommand = CreateCommand(RequestPermissionAsync);
        RefreshDevicesCommand = CreateCommand(RefreshDevicesAsync);
        OpenCameraCommand = CreateCommand(OpenCameraAsync, () => SelectedDevice is not null && !IsCameraOpen);
        CloseCameraCommand = CreateCommand(CloseCameraAsync, () => IsCameraOpen);
        RestartCameraCommand = CreateCommand(RestartCameraAsync, () => SelectedDevice is not null);
        RefreshCapabilitiesCommand = CreateCommand(RefreshCapabilitiesAsync, () => IsCameraOpen);
        RefreshControlStateCommand = CreateCommand(RefreshControlStateAsync, () => IsCameraOpen);
        ApplyExposureModeCommand = CreateCommand(ApplyExposureModeAsync, () => IsCameraOpen && HasExposureModes);
        ApplyExposureCompensationCommand = CreateCommand(ApplyExposureCompensationAsync, () => IsCameraOpen && HasExposureCompensation);
        ApplyManualExposureCommand = CreateCommand(ApplyManualExposureAsync, () => IsCameraOpen && HasManualExposure);
        ApplyFocusModeCommand = CreateCommand(ApplyFocusModeAsync, () => IsCameraOpen && HasFocusModes);
        ApplyFocusPositionCommand = CreateCommand(ApplyFocusPositionAsync, () => IsCameraOpen && HasManualFocus);
        ApplyZoomCommand = CreateCommand(ApplyZoomAsync, () => IsCameraOpen && HasZoom);
        ApplyLightCommand = CreateCommand(ApplyLightAsync, () => IsCameraOpen && HasLight);
        SaveSnapshotCommand = CreateCommand(SaveSnapshotAsync, () => PreviewSource.HasFrame && SaveFileHandler is not null);
        ExportDiagnosticsCommand = CreateCommand(ExportDiagnosticsAsync, () => SaveFileHandler is not null);
        ClearLogCommand = new RelayCommand(ClearLog);

        _permissionStatus = SafeGetPermissionStatus();
        _lastStatsTimestamp = Stopwatch.GetTimestamp();
        _statsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, OnStatsTick);

        Log($"ThinCam Avalonia demo started on {_platformServices.PlatformDescription}.");
        Log($"Initial permission status: {_permissionStatus}.");
    }

    /// <summary>Gets the currently open camera, or null when none is open.</summary>
    public Camera? ActiveCamera { get; private set; }

    /// <summary>Gets a description of the active frame format.</summary>
    public string ActiveFormatText
    {
        get => _activeFormatText;
        private set => SetProperty(ref _activeFormatText, value);
    }

    /// <summary>Gets the command that applies exposure compensation.</summary>
    public AsyncCommand ApplyExposureCompensationCommand { get; }

    /// <summary>Gets the command that applies the selected exposure mode.</summary>
    public AsyncCommand ApplyExposureModeCommand { get; }

    /// <summary>Gets the command that applies the selected focus mode.</summary>
    public AsyncCommand ApplyFocusModeCommand { get; }

    /// <summary>Gets the command that applies the manual focus position.</summary>
    public AsyncCommand ApplyFocusPositionCommand { get; }

    /// <summary>Gets the command that applies light settings.</summary>
    public AsyncCommand ApplyLightCommand { get; }

    /// <summary>Gets the command that applies manual exposure settings.</summary>
    public AsyncCommand ApplyManualExposureCommand { get; }

    /// <summary>Gets the command that applies the zoom factor.</summary>
    public AsyncCommand ApplyZoomCommand { get; }

    private IReadOnlyList<AsyncCommand> AsyncCommands
        =>
        [
            RequestPermissionCommand, RefreshDevicesCommand, OpenCameraCommand, CloseCameraCommand, RestartCameraCommand, RefreshCapabilitiesCommand,
            RefreshControlStateCommand, ApplyExposureModeCommand, ApplyExposureCompensationCommand, ApplyManualExposureCommand, ApplyFocusModeCommand,
            ApplyFocusPositionCommand, ApplyZoomCommand, ApplyLightCommand, SaveSnapshotCommand, ExportDiagnosticsCommand
        ];

    /// <summary>Gets the command that clears the diagnostic log.</summary>
    public RelayCommand ClearLogCommand { get; }

    /// <summary>Gets the command that closes the active camera.</summary>
    public AsyncCommand CloseCameraCommand { get; }

    /// <summary>Gets the available camera devices.</summary>
    public ObservableCollection<CameraDeviceItem> Devices { get; } = [];

    /// <summary>Gets the accumulated diagnostic log.</summary>
    public string DiagnosticText
    {
        get => _diagnosticText;
        private set => SetProperty(ref _diagnosticText, value);
    }

    /// <summary>Gets the command that exports diagnostic information.</summary>
    public AsyncCommand ExportDiagnosticsCommand { get; }

    /// <summary>Gets or sets the requested exposure compensation in EV.</summary>
    public double ExposureCompensation
    {
        get => _exposureCompensation;
        set => SetProperty(ref _exposureCompensation, value);
    }

    /// <summary>Gets the maximum exposure compensation in EV.</summary>
    public double ExposureCompensationMaximum
    {
        get => _exposureCompensationMaximum;
        private set => SetProperty(ref _exposureCompensationMaximum, value);
    }

    /// <summary>Gets the minimum exposure compensation in EV.</summary>
    public double ExposureCompensationMinimum
    {
        get => _exposureCompensationMinimum;
        private set => SetProperty(ref _exposureCompensationMinimum, value);
    }

    /// <summary>Gets the exposure compensation increment in EV.</summary>
    public double ExposureCompensationStep
    {
        get => _exposureCompensationStep;
        private set => SetProperty(ref _exposureCompensationStep, value);
    }

    /// <summary>Gets the supported exposure modes.</summary>
    public ObservableCollection<ExposureMode> ExposureModes { get; } = [];

    /// <summary>Gets the supported focus modes.</summary>
    public ObservableCollection<FocusMode> FocusModes { get; } = [];

    /// <summary>Gets or sets the requested manual focus position.</summary>
    public double FocusPosition
    {
        get => _focusPosition;
        set => SetProperty(ref _focusPosition, value);
    }

    /// <summary>Gets whether an error message is available.</summary>
    public bool HasError => !String.IsNullOrEmpty(LastError);

    /// <summary>Gets whether exposure compensation is available.</summary>
    public bool HasExposureCompensation
    {
        get => _hasExposureCompensation;
        private set => SetCapability(ref _hasExposureCompensation, value);
    }

    /// <summary>Gets whether exposure modes are available.</summary>
    public bool HasExposureModes
    {
        get => _hasExposureModes;
        private set => SetCapability(ref _hasExposureModes, value);
    }

    /// <summary>Gets whether focus modes are available.</summary>
    public bool HasFocusModes
    {
        get => _hasFocusModes;
        private set => SetCapability(ref _hasFocusModes, value);
    }

    /// <summary>Gets whether manual ISO control is available.</summary>
    public bool HasIso
    {
        get => _hasIso;
        private set => SetProperty(ref _hasIso, value);
    }

    /// <summary>Gets whether a controllable camera light is available.</summary>
    public bool HasLight
    {
        get => _hasLight;
        private set => SetCapability(ref _hasLight, value);
    }

    /// <summary>Gets whether manual exposure duration is available.</summary>
    public bool HasManualExposure
    {
        get => _hasManualExposure;
        private set => SetCapability(ref _hasManualExposure, value);
    }

    /// <summary>Gets whether manual focus control is available.</summary>
    public bool HasManualFocus
    {
        get => _hasManualFocus;
        private set => SetCapability(ref _hasManualFocus, value);
    }

    /// <summary>Gets whether variable camera light levels are available.</summary>
    public bool HasVariableLight
    {
        get => _hasVariableLight;
        private set => SetProperty(ref _hasVariableLight, value);
    }

    /// <summary>Gets whether zoom control is available.</summary>
    public bool HasZoom
    {
        get => _hasZoom;
        private set => SetCapability(ref _hasZoom, value);
    }

    /// <summary>Gets whether a camera is currently open.</summary>
    public bool IsCameraOpen => ActiveCamera is not null;

    /// <summary>Gets the most recent error description.</summary>
    public string LastError
    {
        get => _lastError;
        private set
        {
            if (SetProperty(ref _lastError, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>Gets the age of the most recently received frame.</summary>
    public string LastFrameAge
    {
        get => _lastFrameAge;
        private set => SetProperty(ref _lastFrameAge, value);
    }

    /// <summary>Gets or sets whether the camera light should be enabled.</summary>
    public bool LightEnabled
    {
        get => _lightEnabled;
        set => SetProperty(ref _lightEnabled, value);
    }

    /// <summary>Gets or sets the requested camera light level.</summary>
    public double LightLevel
    {
        get => _lightLevel;
        set => SetProperty(ref _lightLevel, value);
    }

    /// <summary>Gets or sets the requested manual exposure duration in milliseconds.</summary>
    public decimal ManualExposureMilliseconds
    {
        get => _manualExposureMilliseconds;
        set => SetProperty(ref _manualExposureMilliseconds, value);
    }

    /// <summary>Gets or sets the requested manual ISO value.</summary>
    public decimal ManualIso
    {
        get => _manualIso;
        set => SetProperty(ref _manualIso, value);
    }

    /// <summary>Gets the command that opens the selected camera.</summary>
    public AsyncCommand OpenCameraCommand { get; }

    /// <summary>Gets the current camera permission status.</summary>
    public CameraPermissionStatus PermissionStatus
    {
        get => _permissionStatus;
        private set
        {
            if (SetProperty(ref _permissionStatus, value))
                OnPropertyChanged(nameof(PermissionStatusText));
        }
    }

    /// <summary>Gets the camera permission status as text.</summary>
    public string PermissionStatusText => PermissionStatus.ToString();

    /// <summary>Gets a description of the current platform.</summary>
    public string PlatformDescription => _platformServices.PlatformDescription;

    /// <summary>Gets the estimated number of preview frames not rendered.</summary>
    public long PreviewFramesSkipped
    {
        get => _previewFramesSkipped;
        private set => SetProperty(ref _previewFramesSkipped, value);
    }

    /// <summary>Gets the source that supplies camera preview frames.</summary>
    public CameraPreviewSource PreviewSource { get; }

    /// <summary>Gets or sets the requested frame queue capacity.</summary>
    public decimal QueueCapacity
    {
        get => _queueCapacity;
        set => SetProperty(ref _queueCapacity, value);
    }

    /// <summary>Gets the measured incoming frame rate.</summary>
    public double ReceivedFramesPerSecond
    {
        get => _receivedFramesPerSecond;
        private set => SetProperty(ref _receivedFramesPerSecond, value);
    }

    /// <summary>Gets the command that refreshes camera capabilities.</summary>
    public AsyncCommand RefreshCapabilitiesCommand { get; }

    /// <summary>Gets the command that refreshes camera control state.</summary>
    public AsyncCommand RefreshControlStateCommand { get; }

    /// <summary>Gets the command that refreshes the camera device list.</summary>
    public AsyncCommand RefreshDevicesCommand { get; }

    /// <summary>Gets the total number of rendered frames.</summary>
    public long RenderedFrames
    {
        get => _renderedFrames;
        private set => SetProperty(ref _renderedFrames, value);
    }

    /// <summary>Gets the measured rendered frame rate.</summary>
    public double RenderedFramesPerSecond
    {
        get => _renderedFramesPerSecond;
        private set => SetProperty(ref _renderedFramesPerSecond, value);
    }

    /// <summary>Gets the command that requests camera permission.</summary>
    public AsyncCommand RequestPermissionCommand { get; }

    /// <summary>Gets or sets the requested frame rate.</summary>
    public decimal RequestedFramesPerSecond
    {
        get => _requestedFramesPerSecond;
        set => SetProperty(ref _requestedFramesPerSecond, value);
    }

    /// <summary>Gets or sets the requested frame height.</summary>
    public decimal RequestedHeight
    {
        get => _requestedHeight;
        set => SetProperty(ref _requestedHeight, value);
    }

    /// <summary>Gets or sets the requested frame width.</summary>
    public decimal RequestedWidth
    {
        get => _requestedWidth;
        set => SetProperty(ref _requestedWidth, value);
    }

    /// <summary>Gets the command that restarts the selected camera.</summary>
    public AsyncCommand RestartCameraCommand { get; }

    /// <summary>Gets or sets the platform-specific file-save handler.</summary>
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

    /// <summary>Gets the command that saves the current preview frame.</summary>
    public AsyncCommand SaveSnapshotCommand { get; }

    /// <summary>Gets or sets the selected camera device.</summary>
    public CameraDeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(SelectedDeviceId));
                NotifyCommandStates();
            }
        }
    }

    /// <summary>Gets the selected platform camera identifier.</summary>
    public string SelectedDeviceId => SelectedDevice?.Device.Id ?? "None";

    /// <summary>Gets or sets the selected exposure mode.</summary>
    public ExposureMode? SelectedExposureMode
    {
        get => _selectedExposureMode;
        set => SetProperty(ref _selectedExposureMode, value);
    }

    /// <summary>Gets or sets the selected focus mode.</summary>
    public FocusMode? SelectedFocusMode
    {
        get => _selectedFocusMode;
        set => SetProperty(ref _selectedFocusMode, value);
    }

    /// <summary>Gets the current operation status.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Gets the total number of received frames.</summary>
    public long TotalFrames
    {
        get => _totalFrames;
        private set => SetProperty(ref _totalFrames, value);
    }

    /// <summary>Gets or sets the requested zoom factor.</summary>
    public double ZoomFactor
    {
        get => _zoomFactor;
        set => SetProperty(ref _zoomFactor, value);
    }

    /// <summary>Gets the maximum supported zoom factor.</summary>
    public double ZoomMaximum
    {
        get => _zoomMaximum;
        private set => SetProperty(ref _zoomMaximum, value);
    }

    /// <summary>Gets the minimum supported zoom factor.</summary>
    public double ZoomMinimum
    {
        get => _zoomMinimum;
        private set => SetProperty(ref _zoomMinimum, value);
    }

    /// <summary>Gets the supported zoom increment.</summary>
    public double ZoomStep
    {
        get => _zoomStep;
        private set => SetProperty(ref _zoomStep, value);
    }

    #region IAsyncDisposable

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    #endregion

    /// <summary>Starts UI statistics updates while the view is active.</summary>
    public void Activate()
    {
        ThrowIfDisposed();
        _statsTimer.Start();
    }

    /// <summary>Stops capture and recurrent UI work when the app leaves the foreground.</summary>
    /// <returns>A task that completes after the camera has stopped.</returns>
    public async Task DeactivateAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _statsTimer.Stop();

        // Cancel first so gate holders release quickly, then release the camera before draining
        // commands that cannot be cancelled, such as an open platform file picker.
        var commands = CancelCommandsAsync();
        await StopCameraAsync().ConfigureAwait(true);
        await commands.ConfigureAwait(true);
    }

    private async Task StopCameraAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await StopCameraCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Records that the preview rendered a frame.</summary>
    public void ReportFrameRendered() => Interlocked.Increment(ref _renderedCounter);

    private async Task RequestPermissionAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            StatusText = "Requesting camera permission…";
            PermissionStatus = await _platformServices
                                     .RequestCameraPermissionAsync(cancellationToken)
                                     .ConfigureAwait(true);
            StatusText = $"Permission: {PermissionStatus}";
            Log($"Permission request completed: {PermissionStatus}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            HandleError("Permission request failed", exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RefreshDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusText = "Enumerating cameras…";
            var cameras = await Task.Run(() => CameraDevices.Enumerate(),
                                         cancellationToken).ConfigureAwait(true);

            var previousId = SelectedDevice?.Device.Id;
            Devices.Clear();
            foreach (var camera in cameras)
                Devices.Add(new CameraDeviceItem(camera));

            SelectedDevice = Devices.FirstOrDefault(item => item.Device.Id == previousId)
                             ?? Devices.FirstOrDefault(item => item.Device.IsDefault)
                             ?? Devices.FirstOrDefault();

            StatusText = $"Found {Devices.Count} camera(s).";
            Log($"Enumerated {Devices.Count} camera(s).");
            foreach (var item in Devices)
                Log($"Device: {item.Device.Name}; id={item.Device.Id}; position={item.Device.Position}; default={item.Device.IsDefault}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            HandleError("Camera enumeration failed", exception);
        }
    }

    private async Task OpenCameraAsync(CancellationToken cancellationToken)
    {
        // Serialize open, close, restart, and disposal so camera ownership changes atomically.
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await OpenCameraCoreAsync(cancellationToken).ConfigureAwait(true);
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
        catch (OperationCanceledException)
        {
            throw;
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
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await StopCameraCoreAsync().ConfigureAwait(true);
            await OpenCameraCoreAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            HandleError("Could not restart camera", exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task OpenCameraCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ActiveCamera is not null)
            return;
        if (SelectedDevice is null)
            throw new InvalidOperationException("Select a camera first.");

        try
        {
            PermissionStatus = SafeGetPermissionStatus();
            if (PermissionStatus != CameraPermissionStatus.Granted)
            {
                PermissionStatus = await _platformServices
                                         .RequestCameraPermissionAsync(cancellationToken)
                                         .ConfigureAwait(true);
            }
            if (PermissionStatus != CameraPermissionStatus.Granted)
                throw new CameraException(CameraErrorCode.PermissionDenied, $"Camera permission is {PermissionStatus}.");

            var options = new CameraOpenOptions
            {
                Width = ToPositiveInt(RequestedWidth, nameof(RequestedWidth)), Height = ToPositiveInt(RequestedHeight, nameof(RequestedHeight)),
                FramesPerSecond = ToPositiveInt(RequestedFramesPerSecond, nameof(RequestedFramesPerSecond)),
                QueueCapacity = Math.Clamp(ToPositiveInt(QueueCapacity, nameof(QueueCapacity)), 1, 32)
            };

            StatusText = $"Opening {SelectedDevice.Device.Name}…";
            LastError = String.Empty;
            ResetFrameStatistics();
            var camera = await Camera.OpenAsync(SelectedDevice.Device, options, cancellationToken).ConfigureAwait(true);
            ActiveCamera = camera;
            _captureCancellation = new CancellationTokenSource();
            _captureTask = CaptureLoopAsync(camera, _captureCancellation.Token);
            OnPropertyChanged(nameof(IsCameraOpen));
            OnPropertyChanged(nameof(ActiveCamera));
            NotifyCommandStates();
            StatusText = $"Capturing from {camera.Device.Name}.";
            Log($"Camera opened: {camera.Device.Name}; requested={options.Width}x{options.Height}@{options.FramesPerSecond}; queue={options.QueueCapacity}.");
            await ReadCapabilitiesAsync(camera, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Cancellation is expected during shutdown, but a partially opened camera must still be released.
            if (exception is not OperationCanceledException)
                HandleError("Could not open camera", exception);

            try
            {
                await StopCameraCoreAsync().ConfigureAwait(true);
            }
            catch (Exception cleanupException)
            {
                HandleError("Could not clean up the failed camera open", cleanupException);
            }

            if (exception is OperationCanceledException)
                throw;
        }
    }

    private async Task StopCameraCoreAsync()
    {
        var camera = ActiveCamera;
        var cancellation = _captureCancellation;
        var captureTask = _captureTask;
        ActiveCamera = null;
        _captureCancellation = null;
        _captureTask = null;

        Exception? shutdownError = null;
        try
        {
            cancellation?.Cancel();
            if (camera is not null)
            {
                StatusText = "Stopping camera…";
                await camera.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            shutdownError = exception;
        }
        finally
        {
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
                    shutdownError ??= exception;
                }
            }

            cancellation?.Dispose();
            PreviewSource.Clear();
            ClearCapabilities();
            ActiveFormatText = "No active format";
            StatusText = "Camera stopped.";
            OnPropertyChanged(nameof(IsCameraOpen));
            OnPropertyChanged(nameof(ActiveCamera));
            NotifyCommandStates();
            Log("Camera stopped.");
        }

        if (shutdownError is not null)
            throw shutdownError;
    }

    private async Task CaptureLoopAsync(Camera camera, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in camera.GetFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                // Publish copies the pooled frame before this iteration releases its ownership.
                using (frame)
                {
                    PreviewSource.Publish(frame);
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
            Dispatcher.UIThread.Post(() => _ = HandleCaptureFailureAsync(camera, exception));
        }
    }

    private async Task HandleCaptureFailureAsync(Camera camera, Exception exception)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        await _lifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!ReferenceEquals(ActiveCamera, camera))
                return;
            HandleError("Capture failed", exception);
            await StopCameraCoreAsync().ConfigureAwait(true);
        }
        catch (Exception shutdownException)
        {
            HandleError("Could not clean up failed capture", shutdownException);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RefreshCapabilitiesAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await ReadCapabilitiesAsync(GetOpenCamera(), cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ReadCapabilitiesAsync(Camera camera, CancellationToken cancellationToken)
    {
        try
        {
            StatusText = "Reading camera capabilities…";
            var capabilities = await camera
                                     .GetCapabilitiesAsync(cancellationToken)
                                     .ConfigureAwait(true);
            _capabilities = capabilities;
            ApplyCapabilities(capabilities);
            await RefreshControlStateCoreAsync(camera, cancellationToken).ConfigureAwait(true);
            StatusText = "Capabilities loaded.";
            Log(DescribeCapabilities(capabilities));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            HandleError("Could not read camera capabilities", exception);
        }
    }

    private async Task RefreshControlStateAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await RefreshControlStateCoreAsync(GetOpenCamera(), cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RefreshControlStateCoreAsync(Camera camera, CancellationToken cancellationToken)
    {
        try
        {
            if (_capabilities?.Exposure.IsSupported == true)
            {
                var exposure = await camera.Controls.Exposure
                                           .GetStateAsync(cancellationToken).ConfigureAwait(true);
                SelectedExposureMode = exposure.Mode ?? SelectedExposureMode;
                ExposureCompensation = exposure.CompensationEv ?? ExposureCompensation;
                if (exposure.Duration is { } duration)
                    ManualExposureMilliseconds = (decimal) duration.TotalMilliseconds;
                if (exposure.Iso is { } iso)
                    ManualIso = (decimal) iso;
            }

            if (_capabilities?.Focus.IsSupported == true)
            {
                var focus = await camera.Controls.Focus
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
                var light = await camera.Controls.Light
                                        .GetStateAsync(cancellationToken).ConfigureAwait(true);
                LightEnabled = light.IsEnabled ?? LightEnabled;
                LightLevel = light.Level ?? LightLevel;
            }

            Log("Control state refreshed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            HandleError("Could not read control state", exception);
        }
    }

    private async Task ApplyExposureModeAsync(CancellationToken cancellationToken)
    {
        if (SelectedExposureMode is not { } mode)
            return;

        await ExecuteControlAsync($"Exposure mode {mode}",
                                  token => GetOpenCamera().Controls.Exposure.SetModeAsync(mode, token).AsTask(),
                                  cancellationToken).ConfigureAwait(true);
    }

    private Task ApplyExposureCompensationAsync(CancellationToken cancellationToken)
        => ExecuteControlAsync($"Exposure compensation {ExposureCompensation:+0.###;-0.###;0} EV",
                               token => GetOpenCamera().Controls.Exposure
                                                       .SetCompensationAsync(ExposureCompensation, token).AsTask(),
                               cancellationToken);

    private Task ApplyManualExposureAsync(CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromMilliseconds((double) ManualExposureMilliseconds);
        double? iso = HasIso ? (double) ManualIso : null;
        return ExecuteControlAsync($"Manual exposure {duration.TotalMilliseconds:0.###} ms" +
                                   (iso.HasValue ? $", ISO {iso.Value:0.##}" : String.Empty),
                                   token => GetOpenCamera().Controls.Exposure
                                                           .SetManualAsync(duration, iso, token).AsTask(),
                                   cancellationToken);
    }

    private async Task ApplyFocusModeAsync(CancellationToken cancellationToken)
    {
        if (SelectedFocusMode is not { } mode)
            return;

        await ExecuteControlAsync($"Focus mode {mode}",
                                  token => GetOpenCamera().Controls.Focus.SetModeAsync(mode, token).AsTask(),
                                  cancellationToken).ConfigureAwait(true);
    }

    private Task ApplyFocusPositionAsync(CancellationToken cancellationToken)
        => ExecuteControlAsync($"Focus position {FocusPosition:0.###}",
                               token => GetOpenCamera().Controls.Focus
                                                       .SetPositionAsync(FocusPosition, token).AsTask(),
                               cancellationToken);

    private Task ApplyZoomAsync(CancellationToken cancellationToken)
        => ExecuteControlAsync($"Zoom {ZoomFactor:0.###}×",
                               token => GetOpenCamera().Controls.Zoom
                                                       .SetFactorAsync(ZoomFactor, token).AsTask(),
                               cancellationToken);

    private async Task ApplyLightAsync(CancellationToken cancellationToken)
    {
        var camera = GetOpenCamera();
        await ExecuteControlAsync($"Light {(LightEnabled ? "enabled" : "disabled")}",
                                  token => camera.Controls.Light.SetEnabledAsync(LightEnabled, token).AsTask(),
                                  cancellationToken).ConfigureAwait(true);

        if (LightEnabled && HasVariableLight)
        {
            await ExecuteControlAsync($"Light level {LightLevel:0.###}",
                                      token => camera.Controls.Light.SetLevelAsync(LightLevel, token).AsTask(),
                                      cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task ExecuteControlAsync(string description,
                                           Func<CancellationToken, Task> operation,
                                           CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            StatusText = $"Applying {description}…";
            await operation(cancellationToken).ConfigureAwait(true);
            StatusText = $"Applied {description}.";
            Log($"Applied {description}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CameraException exception)
        {
            HandleError($"Could not apply {description}", exception);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            HandleError($"Could not apply {description}", exception);
        }
        catch (Exception exception)
        {
            HandleError($"Could not apply {description}", exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task SaveSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SaveFileHandler is null)
                return;

            using var snapshot = PreviewSource.CopySnapshot();
            if (snapshot is null)
            {
                StatusText = "No preview frame is available.";
                return;
            }

            using var image = SKImage.FromBitmap(snapshot)
                              ?? throw new InvalidOperationException("Could not create a Skia image from the preview snapshot.");
            using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                             ?? throw new InvalidOperationException("Could not encode the preview snapshot as PNG.");

            var request = new SaveFileRequest($"thincam-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png",
                                              "PNG image",
                                              "png",
                                              "image/png",
                                              data.ToArray());

            var saved = await SaveFileHandler(request, cancellationToken).ConfigureAwait(true);
            StatusText = saved ? "Snapshot saved." : "Snapshot save cancelled.";
            if (saved)
                Log($"Saved snapshot {snapshot.Width}x{snapshot.Height}.");
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
                return;

            var bytes = Encoding.UTF8.GetBytes(BuildDiagnosticReport());
            var request = new SaveFileRequest($"thincam-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt",
                                              "Text report",
                                              "txt",
                                              "text/plain",
                                              bytes);

            var saved = await SaveFileHandler(request, cancellationToken).ConfigureAwait(true);
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
        foreach (var mode in capabilities.Exposure.Modes.OrderBy(static mode => mode))
            ExposureModes.Add(mode);
        HasExposureModes = ExposureModes.Count != 0;
        SelectedExposureMode = ExposureModes.FirstOrDefault();

        var compensation = capabilities.Exposure.CompensationEv;
        HasExposureCompensation = compensation is not null;
        if (compensation is not null)
        {
            ExposureCompensationMinimum = compensation.Minimum;
            ExposureCompensationMaximum = compensation.Maximum;
            ExposureCompensationStep = PositiveStep(compensation.Step, 0.1);
            ExposureCompensation = compensation.Default;
        }

        var duration = capabilities.Exposure.Duration;
        HasManualExposure = duration is not null;
        if (duration is not null)
            ManualExposureMilliseconds = (decimal) duration.Default.TotalMilliseconds;

        var iso = capabilities.Exposure.Iso;
        HasIso = iso is not null;
        if (iso is not null)
            ManualIso = (decimal) iso.Default;

        FocusModes.Clear();
        foreach (var mode in capabilities.Focus.Modes.OrderBy(static mode => mode))
            FocusModes.Add(mode);
        HasFocusModes = FocusModes.Count != 0;
        SelectedFocusMode = FocusModes.FirstOrDefault();
        HasManualFocus = capabilities.Focus.ManualPosition is not null;
        FocusPosition = capabilities.Focus.ManualPosition?.Default ?? 0.5;

        var zoom = capabilities.Zoom.Factor;
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
        var now = Stopwatch.GetTimestamp();
        var received = Interlocked.Read(ref _receivedCounter);
        var rendered = Interlocked.Read(ref _renderedCounter);
        var seconds = (now - _lastStatsTimestamp) / (double) Stopwatch.Frequency;
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

        var lastFrame = Interlocked.Read(ref _lastFrameTimestamp);
        LastFrameAge = lastFrame == 0
            ? "—"
            : TimeSpan.FromSeconds((now - lastFrame) / (double) Stopwatch.Frequency).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";

        var format = ActiveCamera?.ActiveFormat;
        ActiveFormatText = format is null
            ? ActiveCamera is null ? "No active format" : "Waiting for first frame…"
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
        static string Range<T>(NumericRange<T>? range) => range is null ? "unsupported" : $"{range.Minimum} .. {range.Maximum}; default={range.Default}; step={range.Step}";

        return String.Join(Environment.NewLine,
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
        var joined = String.Join(", ", values);
        return joined.Length == 0 ? "unsupported" : joined;
    }

    private void HandleError(string context, Exception exception)
    {
        LastError = exception switch
        {
            CameraException cameraException => cameraException.GetUserMessage(),
            ArgumentOutOfRangeException => $"{context}: the value is outside the supported range.",
            _ => $"{context}: {exception.Message}"
        };
        StatusText = context + ".";
        Log($"ERROR: {LastError}");
    }

    internal void Log(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            // DiagnosticText is UI-bound, so marshal all log mutations to the UI thread.
            Dispatcher.UIThread.Post(() => Log(message));
            return;
        }

        _logLines.Add($"{DateTimeOffset.Now:HH:mm:ss.fff}  {message}");
        if (_logLines.Count > 400)
            _logLines.RemoveRange(0, _logLines.Count - 400);
        DiagnosticText = String.Join(Environment.NewLine, _logLines);
    }

    private void ClearLog()
    {
        _logLines.Clear();
        DiagnosticText = String.Empty;
    }

    private bool SetCapability(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
            return false;

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

    private AsyncCommand CreateCommand(Func<CancellationToken, Task> execute,
                                       Func<bool>? canExecute = null)
        => new AsyncCommand(execute, canExecute, exception => HandleError("Command failed", exception));

    private Task CancelCommandsAsync()
    {
        // Cancel and capture every execution synchronously so a command started later is unaffected.
        var executions = new List<Task>(AsyncCommands.Count);
        foreach (var command in AsyncCommands)
            executions.Add(command.CancelAsync());

        return Task.WhenAll(executions);
    }

    private static int ToPositiveInt(decimal value, string name)
    {
        if (value <= 0 || value > Int32.MaxValue)
            throw new ArgumentOutOfRangeException(name);
        return Decimal.ToInt32(Decimal.Truncate(value));
    }

    private static double PositiveStep(double? value, double fallback)
        => value.HasValue && Double.IsFinite(value.Value) && value.Value > 0
            ? value.Value
            : fallback;

    private Camera GetOpenCamera() => ActiveCamera ?? throw new InvalidOperationException("Open a camera first.");

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

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _statsTimer.Stop();

        // Stop the camera before draining commands so shutdown cannot wait on a platform picker.
        var commands = CancelCommandsAsync();
        try
        {
            await StopCameraAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            HandleError("Camera shutdown failed", exception);
        }

        try
        {
            await commands.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            HandleError("A command failed during shutdown", exception);
        }
        finally
        {
            PreviewSource.Dispose();
        }
    }
}
