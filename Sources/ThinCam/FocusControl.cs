using ThinCam.Interop;

namespace ThinCam;

/// <summary>Controls camera focus settings.</summary>
public sealed class FocusControl
{
    private readonly Camera _camera;

    internal FocusControl(Camera camera)
    {
        _camera = camera;
    }

    /// <summary>Gets the current focus state.</summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The current focus state.</returns>
    /// <exception cref="CameraException">The camera does not support focus controls or the operation failed.</exception>
    public async ValueTask<FocusState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var values =
            await _camera.GetControlValuesAsync([NativeControlId.FocusMode, NativeControlId.FocusPosition],
                                                cancellationToken).ConfigureAwait(false);

        return new FocusState(values.TryGetValue(NativeControlId.FocusMode, out var mode)
                                  ? (FocusMode) mode.Value.EnumValue
                                  : null,
                              values.TryGetValue(NativeControlId.FocusPosition, out var position)
                                  ? position.Value.DoubleValue
                                  : null);
    }

    /// <summary>Sets the focus mode.</summary>
    /// <param name="mode">The desired focus mode.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode" /> is not a valid enum value.</exception>
    /// <exception cref="CameraException">The camera does not support this focus mode or the operation failed.</exception>
    public ValueTask SetModeAsync(FocusMode mode,
                                  CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Invalid focus mode value.");

        _camera.EnsureControlSupported(NativeControlId.FocusMode);
        _camera.EnsureControlSupported(NativeControlId.FocusMode, mode);
        return _camera.SetControlAsync(NativeControlValue.Enum(NativeControlId.FocusMode, (int) mode),
                                       cancellationToken);
    }

    /// <summary>Sets the normalized manual focus position.</summary>
    /// <param name="position">The normalized focus position (0 = nearest, 1 = infinity).</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position" /> is not in [0, 1].</exception>
    /// <exception cref="CameraException">The camera does not support manual focus or the value is out of range.</exception>
    public ValueTask SetPositionAsync(double position,
                                      CancellationToken cancellationToken = default)
    {
        if (!Double.IsFinite(position) || position < 0 || position > 1)
            throw new ArgumentOutOfRangeException(nameof(position), position, "Focus position must be between 0 and 1.");

        _camera.EnsureControlSupported(NativeControlId.FocusPosition);
        _camera.EnsureControlInRange(NativeControlId.FocusPosition, position);
        return
            _camera.SetControlsAsync([NativeControlValue.Enum(NativeControlId.FocusMode, (int) FocusMode.Manual), NativeControlValue.Double(NativeControlId.FocusPosition, position)],
                                     cancellationToken);
    }
}
