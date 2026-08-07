namespace ThinCam;

/// <summary>
/// Provides access to a camera's adjustable controls.
/// </summary>
public sealed class CameraControls
{
    internal CameraControls(Camera camera)
    {
        Exposure = new ExposureControl(camera);
        Focus = new FocusControl(camera);
        Zoom = new ZoomControl(camera);
        Light = new CameraLightControl(camera);
    }

    /// <summary>
    /// Gets the exposure control.
    /// </summary>
    public ExposureControl Exposure { get; }

    /// <summary>
    /// Gets the focus control.
    /// </summary>
    public FocusControl Focus { get; }

    /// <summary>
    /// Gets the camera light control.
    /// </summary>
    public CameraLightControl Light { get; }

    /// <summary>
    /// Gets the zoom control.
    /// </summary>
    public ZoomControl Zoom { get; }
}
