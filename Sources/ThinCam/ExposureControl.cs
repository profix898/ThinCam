using ThinCam.Interop;

namespace ThinCam;

/// <summary>Controls camera exposure settings.</summary>
public sealed class ExposureControl
{
    private readonly Camera _camera;

    internal ExposureControl(Camera camera) => _camera = camera;

    /// <summary>Gets the current exposure state.</summary>
    public async ValueTask<ExposureState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var values =
            await _camera
                  .GetControlValuesAsync([NativeControlId.ExposureMode, NativeControlId.ExposureCompensationEv, NativeControlId.ExposureDurationMicroseconds, NativeControlId.ExposureIso],
                                         cancellationToken).ConfigureAwait(false);

        return new ExposureState(values.TryGetValue(NativeControlId.ExposureMode, out var mode)
                                     ? (ExposureMode) mode.Value.EnumValue
                                     : null,
                                 values.TryGetValue(NativeControlId.ExposureCompensationEv, out var compensation)
                                     ? compensation.Value.DoubleValue
                                     : null,
                                 values.TryGetValue(NativeControlId.ExposureDurationMicroseconds, out var duration)
                                     ? TimeSpan.FromMicroseconds(duration.Value.IntegerValue)
                                     : null,
                                 values.TryGetValue(NativeControlId.ExposureIso, out var iso)
                                     ? iso.Value.DoubleValue
                                     : null);
    }

    /// <summary>Sets the exposure mode.</summary>
    public ValueTask SetModeAsync(ExposureMode mode,
                                  CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        return _camera.SetControlAsync(NativeControlValue.Enum(NativeControlId.ExposureMode, (int) mode),
                                       cancellationToken);
    }

    /// <summary>Sets exposure compensation in exposure-value units.</summary>
    public ValueTask SetCompensationAsync(double ev,
                                          CancellationToken cancellationToken = default)
    {
        if (!Double.IsFinite(ev))
            throw new ArgumentOutOfRangeException(nameof(ev));
        return _camera.SetControlAsync(NativeControlValue.Double(NativeControlId.ExposureCompensationEv, ev),
                                       cancellationToken);
    }

    /// <summary>Sets manual exposure duration and optional ISO sensitivity.</summary>
    public ValueTask SetManualAsync(TimeSpan duration,
                                    double? iso = null,
                                    CancellationToken cancellationToken = default)
    {
        if (duration.Ticks < TimeSpan.TicksPerMicrosecond)
        {
            throw new ArgumentOutOfRangeException(nameof(duration),
                                                  "Manual exposure duration must be at least one microsecond.");
        }
        if (iso is <= 0 || (iso.HasValue && !Double.IsFinite(iso.Value)))
            throw new ArgumentOutOfRangeException(nameof(iso));

        var values = new List<NativeControlValue>(3)
        {
            NativeControlValue.Enum(NativeControlId.ExposureMode, (int) ExposureMode.Manual), NativeControlValue.Int64(NativeControlId.ExposureDurationMicroseconds,
                                                                                                                       checked(duration.Ticks / TimeSpan.TicksPerMicrosecond))
        };
        if (iso.HasValue)
            values.Add(NativeControlValue.Double(NativeControlId.ExposureIso, iso.Value));

        return _camera.SetControlsAsync(values, cancellationToken);
    }
}
