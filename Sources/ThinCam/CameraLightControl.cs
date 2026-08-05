using ThinCam.Interop;

namespace ThinCam;

/// <summary>Controls the camera's light and its intensity.</summary>
public sealed class CameraLightControl
{
    private readonly Camera _camera;

    internal CameraLightControl(Camera camera) => _camera = camera;

    /// <summary>Gets the current camera light state.</summary>
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
    public ValueTask SetEnabledAsync(bool enabled,
                                     CancellationToken cancellationToken = default)
        => _camera.SetControlAsync(NativeControlValue.Boolean(NativeControlId.LightEnabled, enabled),
                                   cancellationToken);

    /// <summary>Sets the normalized camera light intensity.</summary>
    public ValueTask SetLevelAsync(double level,
                                   CancellationToken cancellationToken = default)
    {
        if (!Double.IsFinite(level) || level < 0 || level > 1)
            throw new ArgumentOutOfRangeException(nameof(level));

        return _camera.SetControlAsync(NativeControlValue.Double(NativeControlId.LightLevel, level),
                                       cancellationToken);
    }
}
