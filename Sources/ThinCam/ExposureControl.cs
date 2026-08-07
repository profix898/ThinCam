using ThinCam.Interop;

namespace ThinCam;

/// <summary>Controls camera exposure settings.</summary>
public sealed class ExposureControl
{
    private readonly Camera _camera;

    internal ExposureControl(Camera camera)
    {
        _camera = camera;
    }

    /// <summary>Gets the current exposure state.</summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The current exposure state.</returns>
    /// <exception cref="CameraException">The camera does not support exposure controls or the operation failed.</exception>
    public async ValueTask<ExposureState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var values =
            await _camera
                  .GetControlValuesAsync([
                                             NativeControlId.ExposureMode, NativeControlId.ExposureCompensationEv, NativeControlId.ExposureDurationMicroseconds,
                                             NativeControlId.ExposureIso
                                         ],
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
    /// <param name="mode">The desired exposure mode.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode" /> is not a valid enum value.</exception>
    /// <exception cref="CameraException">The camera does not support this exposure mode or the operation failed.</exception>
    public ValueTask SetModeAsync(ExposureMode mode,
                                  CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Invalid exposure mode value.");

        _camera.EnsureControlSupported(NativeControlId.ExposureMode);
        _camera.EnsureControlSupported(NativeControlId.ExposureMode, mode);
        return _camera.SetControlAsync(NativeControlValue.Enum(NativeControlId.ExposureMode, (int) mode),
                                       cancellationToken);
    }

    /// <summary>Sets exposure compensation in exposure-value units.</summary>
    /// <param name="ev">The exposure compensation in EV.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ev" /> is not a finite number.</exception>
    /// <exception cref="CameraException">The camera does not support exposure compensation or the value is out of range.</exception>
    public ValueTask SetCompensationAsync(double ev,
                                          CancellationToken cancellationToken = default)
    {
        if (!Double.IsFinite(ev))
            throw new ArgumentOutOfRangeException(nameof(ev), "Exposure compensation must be a finite number.");

        _camera.EnsureControlSupported(NativeControlId.ExposureCompensationEv);
        _camera.EnsureControlInRange(NativeControlId.ExposureCompensationEv, ev);
        return _camera.SetControlAsync(NativeControlValue.Double(NativeControlId.ExposureCompensationEv, ev),
                                       cancellationToken);
    }

    /// <summary>Sets manual exposure duration and optional ISO sensitivity.</summary>
    /// <param name="duration">The exposure duration.</param>
    /// <param name="iso">The optional ISO sensitivity.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration" /> or <paramref name="iso" /> is invalid.</exception>
    /// <exception cref="CameraException">The camera does not support manual exposure or the values are out of range.</exception>
    public ValueTask SetManualAsync(TimeSpan duration,
                                    double? iso = null,
                                    CancellationToken cancellationToken = default)
    {
        if (duration.Ticks < TimeSpan.TicksPerMicrosecond)
        {
            throw new ArgumentOutOfRangeException(nameof(duration),
                                                  duration,
                                                  "Manual exposure duration must be at least one microsecond.");
        }

        if (iso is <= 0 || (iso.HasValue && !Double.IsFinite(iso.Value)))
            throw new ArgumentOutOfRangeException(nameof(iso), iso, "ISO must be a positive finite number.");

        _camera.EnsureControlSupported(NativeControlId.ExposureDurationMicroseconds);
        _camera.EnsureControlInRange(NativeControlId.ExposureDurationMicroseconds, duration);
        if (iso.HasValue)
        {
            _camera.EnsureControlSupported(NativeControlId.ExposureIso);
            _camera.EnsureControlInRange(NativeControlId.ExposureIso, iso.Value);
        }

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
