using ThinCam.Interop;

namespace ThinCam;

/// <summary>Controls the camera's zoom factor.</summary>
public sealed class ZoomControl
{
    private readonly Camera _camera;

    internal ZoomControl(Camera camera)
    {
        _camera = camera;
    }

    /// <summary>Gets the current zoom factor.</summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The current zoom factor.</returns>
    /// <exception cref="CameraException">The camera does not support zoom or the operation failed.</exception>
    public async ValueTask<double> GetFactorAsync(CancellationToken cancellationToken = default)
    {
        var value = await _camera.GetRequiredControlValueAsync(NativeControlId.ZoomFactor,
                                                               cancellationToken).ConfigureAwait(false);
        return value.Value.DoubleValue;
    }

    /// <summary>Sets the zoom factor.</summary>
    /// <param name="factor">The zoom factor (1.0 = no zoom).</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor" /> is not positive or not finite.</exception>
    /// <exception cref="CameraException">The camera does not support zoom or the value is out of range.</exception>
    public ValueTask SetFactorAsync(double factor,
                                    CancellationToken cancellationToken = default)
    {
        if (!Double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Zoom factor must be a positive finite number.");

        _camera.EnsureControlSupported(NativeControlId.ZoomFactor);
        _camera.EnsureControlInRange(NativeControlId.ZoomFactor, factor);
        return _camera.SetControlAsync(NativeControlValue.Double(NativeControlId.ZoomFactor, factor),
                                       cancellationToken);
    }
}
