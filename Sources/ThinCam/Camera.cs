using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ThinCam.Interop;

namespace ThinCam;

/// <summary>Represents an open camera and its frame stream and controls.</summary>
public sealed class Camera : IAsyncDisposable
{
    private readonly SafeCameraHandle _handle;
    private readonly CameraState _state;
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly object _disposeLock = new();
    private CameraCapabilities? _capabilities;
    private Task? _disposeTask;
    private int _readerClaimed;
    private int _disposed;

    private Camera(CameraDevice device,
                   SafeCameraHandle handle,
                   CameraState state)
    {
        Device = device;
        _handle = handle;
        _state = state;
        Controls = new CameraControls(this);
    }

    /// <summary>Gets the active capture format after the first frame arrives.</summary>
    public CameraFormat? ActiveFormat => _state.ActiveFormat;

    /// <summary>Gets the controls exposed by this camera.</summary>
    public CameraControls Controls { get; }

    /// <summary>Gets the device opened by this camera.</summary>
    public CameraDevice Device { get; }

    /// <summary>Gets the last error reported by the camera backend.</summary>
    public CameraException? LastError => _state.LastError;

    #region IAsyncDisposable

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    #endregion

    /// <summary>Opens a camera using the specified options.</summary>
    public static ValueTask<Camera> OpenAsync(CameraDevice device,
                                              CameraOpenOptions? options = null,
                                              CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();

        options ??= new CameraOpenOptions();
        options.Validate();
        NativeHelpers.EnsureAbi();

        var permission = CameraPermissions.GetStatus();
        if (permission != CameraPermissionStatus.Granted)
        {
            throw new CameraException(CameraErrorCode.PermissionDenied,
                                      $"Camera permission is {permission}. Request permission before opening a camera.");
        }

        // Native camera startup is intentionally synchronous so each backend can report a
        // definitive result. Run it off the caller's context because several platform APIs block.
        return new ValueTask<Camera>(Task.Run(() => OpenCore(device, options),
                                              CancellationToken.None));
    }

    private static Camera OpenCore(CameraDevice device, CameraOpenOptions options)
    {
        var state = new CameraState(options.QueueCapacity);
        var stateHandle = GCHandle.Alloc(state);
        nint nativeHandle = 0;
        SafeCameraHandle? safeHandle = null;

        try
        {
            NativeOpenOptions nativeOptions = new()
            {
                StructSize = (uint) Marshal.SizeOf<NativeOpenOptions>(), Width = options.Width, Height = options.Height, FramesPerSecond = options.FramesPerSecond,
                PixelFormat = (NativePixelFormat) (int) options.PixelFormat
            };

            unsafe
            {
                var openResult = NativeMethods.CameraOpen(device.Id,
                                                          in nativeOptions,
                                                          (nint) (delegate* unmanaged[Cdecl]<NativeFrame*, nint, void>) &OnFrame,
                                                          (nint) (delegate* unmanaged[Cdecl]<NativeStatus, byte*, int, nint, void>) &OnError,
                                                          GCHandle.ToIntPtr(stateHandle),
                                                          out nativeHandle);

                if (openResult != NativeStatus.Ok)
                    throw NativeHelpers.Exception(openResult);
            }

            safeHandle = new SafeCameraHandle(nativeHandle, stateHandle);
            nativeHandle = 0;

            var startResult = NativeMethods.CameraStart(safeHandle.DangerousGetHandle());
            if (startResult != NativeStatus.Ok)
                throw NativeHelpers.Exception(startResult);

            // Transfer ownership of the handle and GCHandle to the Camera/SafeHandle.
            stateHandle = default(GCHandle);
            return new Camera(device, safeHandle, state);
        }
        catch
        {
            // safeHandle?.Dispose closes the native camera and frees the GCHandle atomically.
            safeHandle?.Dispose();
            if (nativeHandle != 0)
                NativeMethods.CameraClose(nativeHandle);
            state.Close();
            state.Queue.Drain();
            if (stateHandle.IsAllocated)
                stateHandle.Free();
            throw;
        }
    }

    /// <summary>Gets the camera's frame stream for its single consumer.</summary>
    public IAsyncEnumerable<VideoFrame> GetFramesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _readerClaimed, 1) != 0)
            throw new InvalidOperationException("A ThinCam camera supports one frame consumer.");

        return _state.Queue.ReadAllAsync(cancellationToken);
    }

    /// <summary>Gets the camera's supported control capabilities.</summary>
    public async ValueTask<CameraCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var cached = Volatile.Read(ref _capabilities);
        if (cached is not null)
            return cached;

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cached = Volatile.Read(ref _capabilities);
            if (cached is not null)
                return cached;

            var capabilities = await Task.Run(() => ReadCapabilitiesCore(_handle.DangerousGetHandle()),
                                              CancellationToken.None).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _capabilities, capabilities, null);
            return Volatile.Read(ref _capabilities)!;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    internal async ValueTask<IReadOnlyDictionary<NativeControlId, NativeControlValue>> GetControlValuesAsync(IReadOnlyList<NativeControlId> ids,
                                                                                                             CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() =>
            {
                var values = new Dictionary<NativeControlId, NativeControlValue>();
                var handle = _handle.DangerousGetHandle();
                foreach (var id in ids)
                {
                    var status = NativeMethods.CameraGetControl(handle, id, out var value);
                    if (status == NativeStatus.NotSupported)
                        continue;
                    if (status != NativeStatus.Ok)
                        throw NativeHelpers.Exception(status, ControlDisplayName(id));
                    ValidateControlValue(id, value);
                    values[id] = value;
                }
                return (IReadOnlyDictionary<NativeControlId, NativeControlValue>) values;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    internal async ValueTask<NativeControlValue> GetRequiredControlValueAsync(NativeControlId id,
                                                                              CancellationToken cancellationToken)
    {
        var values =
            await GetControlValuesAsync([id], cancellationToken).ConfigureAwait(false);
        if (!values.TryGetValue(id, out var value))
        {
            throw new CameraException(CameraErrorCode.NotSupported,
                                      $"This camera does not support {ControlDisplayName(id)}.",
                                      ControlDisplayName(id));
        }
        return value;
    }

    internal ValueTask SetControlAsync(NativeControlValue value,
                                       CancellationToken cancellationToken)
        => SetControlsAsync([value], cancellationToken);

    /// <summary>
    /// Validates that a control is supported and the value is within the capability range,
    /// throwing a <see cref="CameraException" /> with a descriptive message if not.
    /// </summary>
    internal void EnsureControlSupported(NativeControlId id)
    {
        var caps = Volatile.Read(ref _capabilities);
        var controlName = ControlDisplayName(id);

        if (caps is null)
            return;

        var supported = id switch
        {
            NativeControlId.ExposureMode => caps.Exposure.Modes.Count > 0,
            NativeControlId.ExposureCompensationEv => caps.Exposure.CompensationEv is not null,
            NativeControlId.ExposureDurationMicroseconds => caps.Exposure.Duration is not null,
            NativeControlId.ExposureIso => caps.Exposure.Iso is not null,
            NativeControlId.FocusMode => caps.Focus.Modes.Count > 0,
            NativeControlId.FocusPosition => caps.Focus.ManualPosition is not null,
            NativeControlId.ZoomFactor => caps.Zoom.Factor is not null,
            NativeControlId.LightEnabled => caps.Light.IsAvailable,
            NativeControlId.LightLevel => caps.Light.Level is not null,
            _ => true
        };

        if (!supported)
        {
            throw new CameraException(CameraErrorCode.NotSupported,
                                      $"This camera does not support {controlName}.",
                                      controlName);
        }
    }

    /// <summary>
    /// Validates a double control value against the capability range.
    /// </summary>
    internal void EnsureControlInRange(NativeControlId id, double value)
    {
        var caps = Volatile.Read(ref _capabilities);
        if (caps is null)
            return;

        var controlName = ControlDisplayName(id);
        var range = id switch
        {
            NativeControlId.ExposureCompensationEv => caps.Exposure.CompensationEv,
            NativeControlId.ExposureIso => caps.Exposure.Iso,
            NativeControlId.FocusPosition => caps.Focus.ManualPosition,
            NativeControlId.ZoomFactor => caps.Zoom.Factor,
            NativeControlId.LightLevel => caps.Light.Level,
            _ => null
        };

        if (range is null)
            return;

        if (value < range.Minimum || value > range.Maximum)
        {
            throw new CameraException(CameraErrorCode.InvalidArgument,
                                      $"{controlName} value {value:0.###} is out of range. Supported: {range.Minimum:0.###} to {range.Maximum:0.###}.",
                                      controlName);
        }
    }

    /// <summary>
    /// Validates a TimeSpan control value against the capability range.
    /// </summary>
    internal void EnsureControlInRange(NativeControlId id, TimeSpan value)
    {
        var caps = Volatile.Read(ref _capabilities);
        if (caps is null)
            return;

        var controlName = ControlDisplayName(id);
        var range = id switch
        {
            NativeControlId.ExposureDurationMicroseconds => caps.Exposure.Duration,
            _ => null
        };

        if (range is null)
            return;

        if (value < range.Minimum || value > range.Maximum)
        {
            throw new CameraException(CameraErrorCode.InvalidArgument,
                                      $"{controlName} value {value.TotalMilliseconds:0.###} ms is out of range. Supported: {range.Minimum.TotalMilliseconds:0.###} ms to {
                                          range.Maximum.TotalMilliseconds:0.###} ms.",
                                      controlName);
        }
    }

    /// <summary>
    /// Validates an enum control value against the supported set.
    /// </summary>
    internal void EnsureControlSupported<TEnum>(NativeControlId id, TEnum value)
        where TEnum : struct, Enum
    {
        var caps = Volatile.Read(ref _capabilities);
        if (caps is null)
            return;

        var controlName = ControlDisplayName(id);
        var supported = id switch
        {
            NativeControlId.ExposureMode => caps.Exposure.Modes as IReadOnlySet<TEnum>,
            NativeControlId.FocusMode => caps.Focus.Modes as IReadOnlySet<TEnum>,
            _ => null
        };

        if (supported is null || supported.Count == 0)
            return;

        if (!supported.Contains(value))
        {
            throw new CameraException(CameraErrorCode.InvalidArgument,
                                      $"{controlName} value {value} is not supported by this camera. Supported: {String.Join(", ", supported)}.",
                                      controlName);
        }
    }

    internal async ValueTask SetControlsAsync(IReadOnlyList<NativeControlValue> values,
                                              CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            return;

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await Task.Run(() =>
            {
                var handle = _handle.DangerousGetHandle();
                foreach (var requested in values)
                {
                    var value = requested;
                    var status = NativeMethods.CameraSetControl(handle, in value);
                    if (status != NativeStatus.Ok)
                    {
                        var controlName = ControlDisplayName(value.Id);
                        throw new CameraException((CameraErrorCode) (int) status,
                                                  $"{controlName}: {NativeHelpers.StatusMessage(status)}",
                                                  controlName);
                    }
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _state.Close();
        await _controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(_handle.Dispose, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _state.Queue.Drain();
            _controlGate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static CameraCapabilities ReadCapabilitiesCore(nint handle)
    {
        var information = new Dictionary<NativeControlId, NativeControlInfo>();
        foreach (var id in Enum.GetValues<NativeControlId>())
        {
            var status = NativeMethods.CameraGetControlInfo(handle, id, out var info);
            if (status == NativeStatus.NotSupported)
                continue;
            if (status != NativeStatus.Ok)
                throw NativeHelpers.Exception(status);

            // Validate the capability structure and discriminant before trusting native ABI data.
            if (info.StructSize < (uint) Marshal.SizeOf<NativeControlInfo>() || info.Id != id ||
                info.ValueType != ExpectedValueType(id))
                throw new CameraException(CameraErrorCode.Platform, "The native backend returned invalid control capability data.");
            information[id] = info;
        }

        var exposureModes = EnumValues<ExposureMode>(WritableInfo(information, NativeControlId.ExposureMode, NativeControlValueType.Enum)
                                                         .SupportedEnumValues);
        var focusModes = EnumValues<FocusMode>(WritableInfo(information, NativeControlId.FocusMode, NativeControlValueType.Enum)
                                                   .SupportedEnumValues);

        var compensation = DoubleRange(WritableInfo(information, NativeControlId.ExposureCompensationEv, NativeControlValueType.Double));
        var duration = TimeRange(WritableInfo(information, NativeControlId.ExposureDurationMicroseconds, NativeControlValueType.Int64));
        var iso = DoubleRange(WritableInfo(information, NativeControlId.ExposureIso, NativeControlValueType.Double));
        var focusPosition = DoubleRange(WritableInfo(information, NativeControlId.FocusPosition, NativeControlValueType.Double));
        var zoom = DoubleRange(WritableInfo(information, NativeControlId.ZoomFactor, NativeControlValueType.Double));
        var lightLevel = DoubleRange(WritableInfo(information, NativeControlId.LightLevel, NativeControlValueType.Double));
        var lightAvailable = WritableInfo(information, NativeControlId.LightEnabled, NativeControlValueType.Boolean).StructSize != 0;

        return new CameraCapabilities(new ExposureCapabilities(exposureModes, compensation, duration, iso),
                                      new FocusCapabilities(focusModes, focusPosition),
                                      new ZoomCapabilities(zoom),
                                      new CameraLightCapabilities(lightAvailable,
                                                                  lightAvailable && lightLevel is not null,
                                                                  lightAvailable ? lightLevel : null));
    }

    private static NativeControlInfo WritableInfo(Dictionary<NativeControlId, NativeControlInfo> information,
                                                  NativeControlId id,
                                                  NativeControlValueType expectedType)
    {
        if (!information.TryGetValue(id, out var info) ||
            info.ValueType != expectedType ||
            (info.Flags & NativeControlFlags.Writable) == 0)
            return default(NativeControlInfo);
        return info;
    }

    private static NativeControlValueType ExpectedValueType(NativeControlId id)
        => id switch
        {
            NativeControlId.ExposureMode => NativeControlValueType.Enum,
            NativeControlId.ExposureCompensationEv => NativeControlValueType.Double,
            NativeControlId.ExposureDurationMicroseconds => NativeControlValueType.Int64,
            NativeControlId.ExposureIso => NativeControlValueType.Double,
            NativeControlId.FocusMode => NativeControlValueType.Enum,
            NativeControlId.FocusPosition => NativeControlValueType.Double,
            NativeControlId.ZoomFactor => NativeControlValueType.Double,
            NativeControlId.LightEnabled => NativeControlValueType.Boolean,
            NativeControlId.LightLevel => NativeControlValueType.Double,
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };

    private static HashSet<TEnum> EnumValues<TEnum>(ulong mask)
        where TEnum : struct, Enum
    {
        var values = new HashSet<TEnum>();
        foreach (var value in Enum.GetValues<TEnum>())
        {
            var numeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (numeric > 0 && numeric < 64 && (mask & (1UL << numeric)) != 0)
                values.Add(value);
        }
        return values;
    }

    private static NumericRange<double>? DoubleRange(NativeControlInfo info)
    {
        if (info.StructSize == 0)
            return null;
        if (!Double.IsFinite(info.Minimum) || !Double.IsFinite(info.Maximum) ||
            !Double.IsFinite(info.DefaultValue) || !Double.IsFinite(info.Step) ||
            info.Maximum < info.Minimum || info.DefaultValue < info.Minimum ||
            info.DefaultValue > info.Maximum || info.Step < 0)
        {
            throw new CameraException(CameraErrorCode.Platform,
                                      $"The native backend returned an invalid range for {ControlDisplayName(info.Id)}.");
        }
        return new NumericRange<double>(info.Minimum, info.Maximum, info.DefaultValue, info.Step);
    }

    private static NumericRange<TimeSpan>? TimeRange(NativeControlInfo info)
    {
        var microseconds = DoubleRange(info);
        if (microseconds is null)
            return null;
        try
        {
            return new NumericRange<TimeSpan>(TimeSpan.FromMicroseconds(microseconds.Minimum),
                                              TimeSpan.FromMicroseconds(microseconds.Maximum),
                                              TimeSpan.FromMicroseconds(microseconds.Default),
                                              TimeSpan.FromMicroseconds(microseconds.Step));
        }
        catch (OverflowException exception)
        {
            throw new CameraException(CameraErrorCode.Platform,
                                      $"The native backend returned an out-of-range duration for {ControlDisplayName(info.Id)}.",
                                      exception);
        }
    }

    private static void ValidateControlValue(NativeControlId requested, NativeControlValue value)
    {
        if (value.StructSize < (uint) Marshal.SizeOf<NativeControlValue>() ||
            value.Id != requested || value.ValueType != ExpectedValueType(requested))
            throw InvalidControlState();

        var valid = requested switch
        {
            NativeControlId.ExposureMode =>
                Enum.IsDefined((ExposureMode) value.Value.EnumValue),
            NativeControlId.FocusMode =>
                Enum.IsDefined((FocusMode) value.Value.EnumValue),
            NativeControlId.ExposureCompensationEv or
                NativeControlId.ExposureIso or
                NativeControlId.FocusPosition or
                NativeControlId.ZoomFactor or
                NativeControlId.LightLevel =>
                Double.IsFinite(value.Value.DoubleValue),
            NativeControlId.ExposureDurationMicroseconds =>
                value.Value.IntegerValue > 0,
            NativeControlId.LightEnabled =>
                value.Value.BooleanValue is 0 or 1,
            _ => false
        };

        if (!valid)
            throw InvalidControlState();

        static CameraException InvalidControlState()
            => new(CameraErrorCode.Platform,
                   "The native backend returned invalid control state data.");
    }

    private static string ControlDisplayName(NativeControlId id)
        => id switch
        {
            NativeControlId.ExposureMode => "exposure mode",
            NativeControlId.ExposureCompensationEv => "exposure compensation",
            NativeControlId.ExposureDurationMicroseconds => "exposure duration",
            NativeControlId.ExposureIso => "ISO sensitivity",
            NativeControlId.FocusMode => "focus mode",
            NativeControlId.FocusPosition => "focus position",
            NativeControlId.ZoomFactor => "zoom",
            NativeControlId.LightEnabled => "light",
            NativeControlId.LightLevel => "light level",
            _ => "camera control"
        };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnFrame(NativeFrame* native, nint userData)
    {
        CameraState? state = null;
        IMemoryOwner<byte>? owner = null;

        try
        {
            if (native is null || native->Data is null)
                return;
            if (native->StructSize < (uint) sizeof(NativeFrame))
                return;
            if (native->DataLength == 0 || native->DataLength > Int32.MaxValue)
                return;
            if (native->Width <= 0 || native->Height <= 0 || native->Stride <= 0)
                return;
            if (native->Stride < native->Width * 4)
                return;
            if (native->PixelFormat != NativePixelFormat.Bgra32)
                throw new InvalidOperationException("The native backend returned an unsupported pixel format.");

            // Validate the native buffer length against the final visible row before copying it.
            var minimumLength = checked(
                ((long) native->Stride * (native->Height - 1)) +
                ((long) native->Width * 4));
            if ((ulong) minimumLength > native->DataLength)
                throw new InvalidOperationException("The native frame buffer is smaller than its dimensions and stride.");

            var handle = GCHandle.FromIntPtr(userData);
            state = handle.Target as CameraState;
            if (state is null || state.IsClosed)
                return;

            var length = checked((int) native->DataLength);
            owner = MemoryPool<byte>.Shared.Rent(length);
            new ReadOnlySpan<byte>(native->Data, length).CopyTo(owner.Memory.Span);

            var frame = new VideoFrame(owner,
                                       length,
                                       native->Width,
                                       native->Height,
                                       native->Stride,
                                       (PixelFormat) (int) native->PixelFormat,
                                       native->RotationDegrees,
                                       native->Mirrored != 0,
                                       TimeSpan.FromMicroseconds(native->TimestampMicroseconds));

            owner = null;
            state.SetActiveFormat(new CameraFormat(frame.Width,
                                                   frame.Height,
                                                   frame.Stride,
                                                   frame.PixelFormat));
            state.Queue.Publish(frame);
        }
        catch (Exception exception)
        {
            owner?.Dispose();
            try
            {
                state?.Fail(new CameraException(CameraErrorCode.Platform,
                                                "Failed to copy a native camera frame.",
                                                exception));
            }
            catch
            {
                // Exceptions must never cross an unmanaged callback boundary.
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnError(NativeStatus status,
                                       byte* message,
                                       int fatal,
                                       nint userData)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not CameraState state)
                return;

            var detail = NativeHelpers.Utf8(message);
            var exception = NativeHelpers.Exception(status, detail);
            state.LastError = exception;
            if (fatal != 0)
                state.Fail(exception);
        }
        catch
        {
            // Exceptions must never cross an unmanaged callback boundary.
        }
    }

    #region Nested: CameraState

    private sealed class CameraState
    {
        private int _closed;

        private CameraFormat? _activeFormat;
        private CameraException? _lastError;

        internal CameraState(int capacity)
        {
            Queue = new FrameQueue(capacity);
        }

        internal CameraFormat? ActiveFormat => Volatile.Read(ref _activeFormat);

        internal bool IsClosed => Volatile.Read(ref _closed) != 0;

        internal CameraException? LastError
        {
            get => Volatile.Read(ref _lastError);
            set => Volatile.Write(ref _lastError, value);
        }

        internal FrameQueue Queue { get; }

        internal void SetActiveFormat(CameraFormat format)
        {
            Interlocked.CompareExchange(ref _activeFormat, format, null);
        }

        internal void Close(Exception? error = null)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
                Queue.Complete(error);
        }

        internal void Fail(CameraException exception)
        {
            LastError = exception;
            Close(exception);
        }
    }

    #endregion
}
