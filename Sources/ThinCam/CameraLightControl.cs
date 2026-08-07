using ThinCam.Interop;

namespace ThinCam;

/// <summary>Controls the camera's light and its intensity.</summary>
public sealed class CameraLightControl
{
    private readonly Camera _camera;

    internal CameraLightControl(Camera camera)
    {
        _camera = camera;
    }

    /// <summary>Gets the current camera light state.</summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The current light state.</returns>
    /// <exception cref="CameraException">The camera does not support a light or the operation failed.</exception>
    public async ValueTask<CameraLightState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var values =
            await _camera.GetControlValuesAsync([NativeControlId.LightEnabled, NativeControlId.LightLevel],
                                                cancellationToken).ConfigureAwait(false);

        return new CameraLightState(values.TryGetValue(NativeControlId.LightEnabled, out var enabled)
                                        ? enabled.Value.BooleanValue != 0
                                        : null,
                                    values.TryGetValue(NativeControlId.LightLevel, out var level)
                                        ? level.Value.DoubleValue
                                        : null);
    }

    /// <summary>Enables or disables the camera light.</summary>
    /// <param name="enabled">Whether the light should be on.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <exception cref="CameraException">The camera does not support a light or the operation failed.</exception>
    public ValueTask SetEnabledAsync(bool enabled,
                                     CancellationToken cancellationToken = default)
    {
        _camera.EnsureControlSupported(NativeControlId.LightEnabled);
        return _camera.SetControlAsync(NativeControlValue.Boolean(NativeControlId.LightEnabled, enabled),
                                       cancellationToken);
    }

    /// <summary>Sets the normalized camera light intensity.</summary>
    /// <param name="level">The light level (0 = off, 1 = maximum brightness).</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level" /> is not in [0, 1].</exception>
    /// <exception cref="CameraException">The camera does not support variable light level or the value is out of range.</exception>
    public ValueTask SetLevelAsync(double level,
                                   CancellationToken cancellationToken = default)
    {
        if (!Double.IsFinite(level) || level < 0 || level > 1)
            throw new ArgumentOutOfRangeException(nameof(level), level, "Light level must be between 0 and 1.");

        _camera.EnsureControlSupported(NativeControlId.LightLevel);
        _camera.EnsureControlInRange(NativeControlId.LightLevel, level);
        return _camera.SetControlAsync(NativeControlValue.Double(NativeControlId.LightLevel, level),
                                       cancellationToken);
    }
}
