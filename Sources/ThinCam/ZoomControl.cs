using ThinCam.Interop;

namespace ThinCam;

/// <summary>Controls the camera's zoom factor.</summary>
public sealed class ZoomControl
{
    private readonly Camera _camera;

    internal ZoomControl(Camera camera) => _camera = camera;

    /// <summary>Gets the current zoom factor.</summary>
    public async ValueTask<double> GetFactorAsync(CancellationToken cancellationToken = default)
    {
        var value = await _camera.GetRequiredControlValueAsync(NativeControlId.ZoomFactor,
                                                               cancellationToken).ConfigureAwait(false);
        return value.Value.DoubleValue;
    }

    /// <summary>Sets the zoom factor.</summary>
    public ValueTask SetFactorAsync(double factor,
                                    CancellationToken cancellationToken = default)
    {
        if (!Double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor));

        return _camera.SetControlAsync(NativeControlValue.Double(NativeControlId.ZoomFactor, factor),
                                       cancellationToken);
    }
}
