using ThinCam.Interop;

namespace ThinCam;

public sealed class CameraControls
{
    internal CameraControls(Camera camera)
    {
        Exposure = new ExposureControl(camera);
        Focus = new FocusControl(camera);
        Zoom = new ZoomControl(camera);
        Light = new CameraLightControl(camera);
    }

    public ExposureControl Exposure { get; }
    public FocusControl Focus { get; }
    public ZoomControl Zoom { get; }
    public CameraLightControl Light { get; }
}

public sealed class ExposureControl
{
    private readonly Camera _camera;

    internal ExposureControl(Camera camera) => _camera = camera;

    public async ValueTask<ExposureState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<NativeControlId, NativeControlValue> values =
            await _camera.GetControlValuesAsync(
                [
                    NativeControlId.ExposureMode,
                    NativeControlId.ExposureCompensationEv,
                    NativeControlId.ExposureDurationMicroseconds,
                    NativeControlId.ExposureIso
                ],
                cancellationToken).ConfigureAwait(false);

        return new ExposureState(
            values.TryGetValue(NativeControlId.ExposureMode, out NativeControlValue mode)
                ? (ExposureMode)mode.Value.EnumValue
                : null,
            values.TryGetValue(NativeControlId.ExposureCompensationEv, out NativeControlValue compensation)
                ? compensation.Value.DoubleValue
                : null,
            values.TryGetValue(NativeControlId.ExposureDurationMicroseconds, out NativeControlValue duration)
                ? TimeSpan.FromMicroseconds(duration.Value.IntegerValue)
                : null,
            values.TryGetValue(NativeControlId.ExposureIso, out NativeControlValue iso)
                ? iso.Value.DoubleValue
                : null);
    }

    public ValueTask SetModeAsync(
        ExposureMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return _camera.SetControlAsync(
            NativeControlValue.Enum(NativeControlId.ExposureMode, (int)mode),
            cancellationToken);
    }

    public ValueTask SetCompensationAsync(
        double ev,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(ev)) throw new ArgumentOutOfRangeException(nameof(ev));
        return _camera.SetControlAsync(
            NativeControlValue.Double(NativeControlId.ExposureCompensationEv, ev),
            cancellationToken);
    }

    public ValueTask SetManualAsync(
        TimeSpan duration,
        double? iso = null,
        CancellationToken cancellationToken = default)
    {
        if (duration.Ticks < TimeSpan.TicksPerMicrosecond)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Manual exposure duration must be at least one microsecond.");
        }
        if (iso is <= 0 || (iso.HasValue && !double.IsFinite(iso.Value)))
        {
            throw new ArgumentOutOfRangeException(nameof(iso));
        }

        var values = new List<NativeControlValue>(3)
        {
            NativeControlValue.Enum(NativeControlId.ExposureMode, (int)ExposureMode.Manual),
            NativeControlValue.Int64(
                NativeControlId.ExposureDurationMicroseconds,
                checked(duration.Ticks / TimeSpan.TicksPerMicrosecond))
        };
        if (iso.HasValue)
        {
            values.Add(NativeControlValue.Double(NativeControlId.ExposureIso, iso.Value));
        }

        return _camera.SetControlsAsync(values, cancellationToken);
    }
}

public sealed class FocusControl
{
    private readonly Camera _camera;

    internal FocusControl(Camera camera) => _camera = camera;

    public async ValueTask<FocusState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<NativeControlId, NativeControlValue> values =
            await _camera.GetControlValuesAsync(
                [NativeControlId.FocusMode, NativeControlId.FocusPosition],
                cancellationToken).ConfigureAwait(false);

        return new FocusState(
            values.TryGetValue(NativeControlId.FocusMode, out NativeControlValue mode)
                ? (FocusMode)mode.Value.EnumValue
                : null,
            values.TryGetValue(NativeControlId.FocusPosition, out NativeControlValue position)
                ? position.Value.DoubleValue
                : null);
    }

    public ValueTask SetModeAsync(
        FocusMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return _camera.SetControlAsync(
            NativeControlValue.Enum(NativeControlId.FocusMode, (int)mode),
            cancellationToken);
    }

    public ValueTask SetPositionAsync(
        double position,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(position) || position < 0 || position > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        return _camera.SetControlsAsync(
            [
                NativeControlValue.Enum(NativeControlId.FocusMode, (int)FocusMode.Manual),
                NativeControlValue.Double(NativeControlId.FocusPosition, position)
            ],
            cancellationToken);
    }
}

public sealed class ZoomControl
{
    private readonly Camera _camera;

    internal ZoomControl(Camera camera) => _camera = camera;

    public async ValueTask<double> GetFactorAsync(
        CancellationToken cancellationToken = default)
    {
        NativeControlValue value = await _camera.GetRequiredControlValueAsync(
            NativeControlId.ZoomFactor,
            cancellationToken).ConfigureAwait(false);
        return value.Value.DoubleValue;
    }

    public ValueTask SetFactorAsync(
        double factor,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        return _camera.SetControlAsync(
            NativeControlValue.Double(NativeControlId.ZoomFactor, factor),
            cancellationToken);
    }
}

public sealed class CameraLightControl
{
    private readonly Camera _camera;

    internal CameraLightControl(Camera camera) => _camera = camera;

    public async ValueTask<CameraLightState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<NativeControlId, NativeControlValue> values =
            await _camera.GetControlValuesAsync(
                [NativeControlId.LightEnabled, NativeControlId.LightLevel],
                cancellationToken).ConfigureAwait(false);

        return new CameraLightState(
            values.TryGetValue(NativeControlId.LightEnabled, out NativeControlValue enabled)
                ? enabled.Value.BooleanValue != 0
                : null,
            values.TryGetValue(NativeControlId.LightLevel, out NativeControlValue level)
                ? level.Value.DoubleValue
                : null);
    }

    public ValueTask SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        _camera.SetControlAsync(
            NativeControlValue.Boolean(NativeControlId.LightEnabled, enabled),
            cancellationToken);

    public ValueTask SetLevelAsync(
        double level,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(level) || level < 0 || level > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        return _camera.SetControlAsync(
            NativeControlValue.Double(NativeControlId.LightLevel, level),
            cancellationToken);
    }
}
