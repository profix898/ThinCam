using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ThinCam.Interop;

namespace ThinCam;

public sealed class Camera : IAsyncDisposable
{
    private readonly SafeCameraHandle _handle;
    private readonly CameraState _state;
    private GCHandle _stateHandle;
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private CameraCapabilities? _capabilities;
    private int _readerClaimed;
    private int _disposed;

    private Camera(
        CameraDevice device,
        SafeCameraHandle handle,
        CameraState state,
        GCHandle stateHandle)
    {
        Device = device;
        _handle = handle;
        _state = state;
        _stateHandle = stateHandle;
        Controls = new CameraControls(this);
    }

    public CameraDevice Device { get; }
    public CameraControls Controls { get; }
    public CameraFormat? ActiveFormat => _state.ActiveFormat;
    public CameraException? LastError => _state.LastError;

    public static ValueTask<Camera> OpenAsync(
        CameraDevice device,
        CameraOpenOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();

        options ??= new CameraOpenOptions();
        options.Validate();
        NativeHelpers.EnsureAbi();

        CameraPermissionStatus permission = CameraPermissions.GetStatus();
        if (permission != CameraPermissionStatus.Granted)
        {
            throw new CameraException(
                CameraErrorCode.PermissionDenied,
                $"Camera permission is {permission}. Request permission before opening a camera.");
        }

        // Native camera startup is intentionally synchronous so each backend can report a
        // definitive result. Run it off the caller's context because several platform APIs block.
        return new ValueTask<Camera>(Task.Run(
            () => OpenCore(device, options),
            CancellationToken.None));
    }

    private static Camera OpenCore(CameraDevice device, CameraOpenOptions options)
    {
        var state = new CameraState(options.QueueCapacity);
        GCHandle stateHandle = GCHandle.Alloc(state);
        nint nativeHandle = 0;

        try
        {
            NativeOpenOptions nativeOptions = new()
            {
                StructSize = (uint)Marshal.SizeOf<NativeOpenOptions>(),
                Width = options.Width,
                Height = options.Height,
                FramesPerSecond = options.FramesPerSecond,
                PixelFormat = (NativePixelFormat)(int)options.PixelFormat
            };

            unsafe
            {
                NativeStatus openResult = NativeMethods.CameraOpen(
                    device.Id,
                    in nativeOptions,
                    (nint)(delegate* unmanaged[Cdecl]<NativeFrame*, nint, void>)&OnFrame,
                    (nint)(delegate* unmanaged[Cdecl]<NativeStatus, byte*, int, nint, void>)&OnError,
                    GCHandle.ToIntPtr(stateHandle),
                    out nativeHandle);

                if (openResult != NativeStatus.Ok)
                {
                    throw NativeHelpers.Exception(openResult);
                }
            }

            var safeHandle = new SafeCameraHandle(nativeHandle);
            nativeHandle = 0;

            NativeStatus startResult = NativeMethods.CameraStart(safeHandle.DangerousGetHandle());
            if (startResult != NativeStatus.Ok)
            {
                safeHandle.Dispose();
                throw NativeHelpers.Exception(startResult);
            }

            return new Camera(device, safeHandle, state, stateHandle);
        }
        catch
        {
            if (nativeHandle != 0) NativeMethods.CameraClose(nativeHandle);
            state.Close();
            state.Queue.Drain();
            if (stateHandle.IsAllocated) stateHandle.Free();
            throw;
        }
    }

    public IAsyncEnumerable<VideoFrame> GetFramesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _readerClaimed, 1) != 0)
        {
            throw new InvalidOperationException("A ThinCam camera supports one frame consumer.");
        }

        return _state.Queue.ReadAllAsync(cancellationToken);
    }

    public async ValueTask<CameraCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        CameraCapabilities? cached = Volatile.Read(ref _capabilities);
        if (cached is not null) return cached;

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cached = Volatile.Read(ref _capabilities);
            if (cached is not null) return cached;

            CameraCapabilities capabilities = await Task.Run(
                () => ReadCapabilitiesCore(_handle.DangerousGetHandle()),
                CancellationToken.None).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _capabilities, capabilities, null);
            return Volatile.Read(ref _capabilities)!;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    internal async ValueTask<IReadOnlyDictionary<NativeControlId, NativeControlValue>> GetControlValuesAsync(
        IReadOnlyList<NativeControlId> ids,
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
                nint handle = _handle.DangerousGetHandle();
                foreach (NativeControlId id in ids)
                {
                    NativeStatus status = NativeMethods.CameraGetControl(handle, id, out NativeControlValue value);
                    if (status == NativeStatus.NotSupported) continue;
                    if (status != NativeStatus.Ok) throw NativeHelpers.Exception(status);
                    ValidateControlValue(id, value);
                    values[id] = value;
                }
                return (IReadOnlyDictionary<NativeControlId, NativeControlValue>)values;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    internal async ValueTask<NativeControlValue> GetRequiredControlValueAsync(
        NativeControlId id,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<NativeControlId, NativeControlValue> values =
            await GetControlValuesAsync([id], cancellationToken).ConfigureAwait(false);
        if (!values.TryGetValue(id, out NativeControlValue value))
        {
            throw new CameraException(
                CameraErrorCode.NotSupported,
                $"The selected camera does not support {ControlDisplayName(id)}.");
        }
        return value;
    }

    internal ValueTask SetControlAsync(
        NativeControlValue value,
        CancellationToken cancellationToken) =>
        SetControlsAsync([value], cancellationToken);

    internal async ValueTask SetControlsAsync(
        IReadOnlyList<NativeControlValue> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return;

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await Task.Run(() =>
            {
                nint handle = _handle.DangerousGetHandle();
                foreach (NativeControlValue requested in values)
                {
                    NativeControlValue value = requested;
                    NativeStatus status = NativeMethods.CameraSetControl(handle, in value);
                    if (status != NativeStatus.Ok)
                    {
                        throw NativeHelpers.Exception(
                            status,
                            $"Could not set {ControlDisplayName(value.Id)}: {NativeHelpers.StatusMessage(status)}");
                    }
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _state.Close();
        await _controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(_handle.Dispose, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _state.Queue.Drain();
            if (_stateHandle.IsAllocated) _stateHandle.Free();
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
        foreach (NativeControlId id in Enum.GetValues<NativeControlId>())
        {
            NativeStatus status = NativeMethods.CameraGetControlInfo(handle, id, out NativeControlInfo info);
            if (status == NativeStatus.NotSupported) continue;
            if (status != NativeStatus.Ok) throw NativeHelpers.Exception(status);
            if (info.StructSize < (uint)Marshal.SizeOf<NativeControlInfo>() || info.Id != id ||
                info.ValueType != ExpectedValueType(id))
            {
                throw new CameraException(CameraErrorCode.Platform, "The native backend returned invalid control capability data.");
            }
            information[id] = info;
        }

        HashSet<ExposureMode> exposureModes = EnumValues<ExposureMode>(
            WritableInfo(information, NativeControlId.ExposureMode, NativeControlValueType.Enum)
                .SupportedEnumValues);
        HashSet<FocusMode> focusModes = EnumValues<FocusMode>(
            WritableInfo(information, NativeControlId.FocusMode, NativeControlValueType.Enum)
                .SupportedEnumValues);

        NumericRange<double>? compensation = DoubleRange(
            WritableInfo(information, NativeControlId.ExposureCompensationEv, NativeControlValueType.Double));
        NumericRange<TimeSpan>? duration = TimeRange(
            WritableInfo(information, NativeControlId.ExposureDurationMicroseconds, NativeControlValueType.Int64));
        NumericRange<double>? iso = DoubleRange(
            WritableInfo(information, NativeControlId.ExposureIso, NativeControlValueType.Double));
        NumericRange<double>? focusPosition = DoubleRange(
            WritableInfo(information, NativeControlId.FocusPosition, NativeControlValueType.Double));
        NumericRange<double>? zoom = DoubleRange(
            WritableInfo(information, NativeControlId.ZoomFactor, NativeControlValueType.Double));
        NumericRange<double>? lightLevel = DoubleRange(
            WritableInfo(information, NativeControlId.LightLevel, NativeControlValueType.Double));
        bool lightAvailable = WritableInfo(
            information, NativeControlId.LightEnabled, NativeControlValueType.Boolean).StructSize != 0;

        return new CameraCapabilities(
            new ExposureCapabilities(exposureModes, compensation, duration, iso),
            new FocusCapabilities(focusModes, focusPosition),
            new ZoomCapabilities(zoom),
            new CameraLightCapabilities(
                lightAvailable,
                lightAvailable && lightLevel is not null,
                lightAvailable ? lightLevel : null));
    }

    private static NativeControlInfo WritableInfo(
        Dictionary<NativeControlId, NativeControlInfo> information,
        NativeControlId id,
        NativeControlValueType expectedType)
    {
        if (!information.TryGetValue(id, out NativeControlInfo info) ||
            info.ValueType != expectedType ||
            (info.Flags & NativeControlFlags.Writable) == 0)
        {
            return default;
        }
        return info;
    }

    private static NativeControlValueType ExpectedValueType(NativeControlId id) => id switch
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
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            int numeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (numeric > 0 && numeric < 64 && (mask & (1UL << numeric)) != 0)
            {
                values.Add(value);
            }
        }
        return values;
    }

    private static NumericRange<double>? DoubleRange(NativeControlInfo info)
    {
        if (info.StructSize == 0) return null;
        if (!double.IsFinite(info.Minimum) || !double.IsFinite(info.Maximum) ||
            !double.IsFinite(info.DefaultValue) || !double.IsFinite(info.Step) ||
            info.Maximum < info.Minimum || info.DefaultValue < info.Minimum ||
            info.DefaultValue > info.Maximum || info.Step < 0)
        {
            throw new CameraException(
                CameraErrorCode.Platform,
                $"The native backend returned an invalid range for {ControlDisplayName(info.Id)}.");
        }
        return new NumericRange<double>(info.Minimum, info.Maximum, info.DefaultValue, info.Step);
    }

    private static NumericRange<TimeSpan>? TimeRange(NativeControlInfo info)
    {
        NumericRange<double>? microseconds = DoubleRange(info);
        if (microseconds is null) return null;
        try
        {
            return new NumericRange<TimeSpan>(
                TimeSpan.FromMicroseconds(microseconds.Minimum),
                TimeSpan.FromMicroseconds(microseconds.Maximum),
                TimeSpan.FromMicroseconds(microseconds.Default),
                TimeSpan.FromMicroseconds(microseconds.Step));
        }
        catch (OverflowException exception)
        {
            throw new CameraException(
                CameraErrorCode.Platform,
                $"The native backend returned an out-of-range duration for {ControlDisplayName(info.Id)}.",
                exception);
        }
    }

    private static void ValidateControlValue(NativeControlId requested, NativeControlValue value)
    {
        if (value.StructSize < (uint)Marshal.SizeOf<NativeControlValue>() ||
            value.Id != requested || value.ValueType != ExpectedValueType(requested))
        {
            throw InvalidControlState();
        }

        bool valid = requested switch
        {
            NativeControlId.ExposureMode =>
                Enum.IsDefined((ExposureMode)value.Value.EnumValue),
            NativeControlId.FocusMode =>
                Enum.IsDefined((FocusMode)value.Value.EnumValue),
            NativeControlId.ExposureCompensationEv or
            NativeControlId.ExposureIso or
            NativeControlId.FocusPosition or
            NativeControlId.ZoomFactor or
            NativeControlId.LightLevel =>
                double.IsFinite(value.Value.DoubleValue),
            NativeControlId.ExposureDurationMicroseconds =>
                value.Value.IntegerValue > 0,
            NativeControlId.LightEnabled =>
                value.Value.BooleanValue is 0 or 1,
            _ => false
        };

        if (!valid) throw InvalidControlState();

        static CameraException InvalidControlState() => new(
            CameraErrorCode.Platform,
            "The native backend returned invalid control state data.");
    }

    private static string ControlDisplayName(NativeControlId id) => id switch
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
            if (native is null || native->Data is null) return;
            if (native->StructSize < (uint)sizeof(NativeFrame)) return;
            if (native->DataLength == 0 || native->DataLength > int.MaxValue) return;
            if (native->Width <= 0 || native->Height <= 0 || native->Stride <= 0) return;
            if (native->PixelFormat != NativePixelFormat.Bgra32) {
                throw new InvalidOperationException("The native backend returned an unsupported pixel format.");
            }

            long minimumLength = checked(
                (long)native->Stride * (native->Height - 1) +
                (long)native->Width * 4);
            if ((ulong)minimumLength > native->DataLength) {
                throw new InvalidOperationException("The native frame buffer is smaller than its dimensions and stride.");
            }

            GCHandle handle = GCHandle.FromIntPtr(userData);
            state = handle.Target as CameraState;
            if (state is null || state.IsClosed) return;

            int length = checked((int)native->DataLength);
            owner = MemoryPool<byte>.Shared.Rent(length);
            new ReadOnlySpan<byte>(native->Data, length).CopyTo(owner.Memory.Span);

            var frame = new VideoFrame(
                owner,
                length,
                native->Width,
                native->Height,
                native->Stride,
                (PixelFormat)(int)native->PixelFormat,
                native->RotationDegrees,
                native->Mirrored != 0,
                TimeSpan.FromMicroseconds(native->TimestampMicroseconds));

            owner = null;
            state.SetActiveFormat(new CameraFormat(
                frame.Width,
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
                state?.Fail(new CameraException(
                    CameraErrorCode.Platform,
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
    private static unsafe void OnError(
        NativeStatus status,
        byte* message,
        int fatal,
        nint userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not CameraState state) return;

            string detail = NativeHelpers.Utf8(message);
            var exception = NativeHelpers.Exception(status, detail);
            state.LastError = exception;
            if (fatal != 0) state.Fail(exception);
        }
        catch
        {
            // Exceptions must never cross an unmanaged callback boundary.
        }
    }

    private sealed class CameraState
    {
        private int _closed;

        internal CameraState(int capacity)
        {
            Queue = new FrameQueue(capacity);
        }

        private CameraFormat? _activeFormat;
        private CameraException? _lastError;

        internal FrameQueue Queue { get; }
        internal CameraFormat? ActiveFormat => Volatile.Read(ref _activeFormat);
        internal CameraException? LastError
        {
            get => Volatile.Read(ref _lastError);
            set => Volatile.Write(ref _lastError, value);
        }
        internal bool IsClosed => Volatile.Read(ref _closed) != 0;

        internal void SetActiveFormat(CameraFormat format)
        {
            Interlocked.CompareExchange(ref _activeFormat, format, null);
        }

        internal void Close(Exception? error = null)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Queue.Complete(error);
            }
        }

        internal void Fail(CameraException exception)
        {
            LastError = exception;
            Close(exception);
        }
    }
}
