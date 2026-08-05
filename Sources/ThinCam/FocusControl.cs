using ThinCam.Interop;

namespace ThinCam;

/// <summary>Controls camera focus settings.</summary>
public sealed class FocusControl
{
    private readonly Camera _camera;

    internal FocusControl(Camera camera) => _camera = camera;

    /// <summary>Gets the current focus state.</summary>
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
    public ValueTask SetModeAsync(FocusMode mode,
                                  CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        return _camera.SetControlAsync(NativeControlValue.Enum(NativeControlId.FocusMode, (int) mode),
                                       cancellationToken);
    }

    /// <summary>Sets the normalized manual focus position.</summary>
    public ValueTask SetPositionAsync(double position,
                                      CancellationToken cancellationToken = default)
    {
        if (!Double.IsFinite(position) || position < 0 || position > 1)
            throw new ArgumentOutOfRangeException(nameof(position));

        return
            _camera.SetControlsAsync([NativeControlValue.Enum(NativeControlId.FocusMode, (int) FocusMode.Manual), NativeControlValue.Double(NativeControlId.FocusPosition, position)],
                                     cancellationToken);
    }
}
