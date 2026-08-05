using ThinCam;

namespace ThinCamDemo.ViewModels;

/// <summary>Represents a camera device displayed in the device list.</summary>
/// <param name="Device">The underlying camera device.</param>
public sealed record CameraDeviceItem(CameraDevice Device)
{
    /// <summary>Gets the user-facing device description.</summary>
    public string DisplayName => $"{Device.Name} ({Device.Position}){(Device.IsDefault ? " — default" : String.Empty)}";

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
