namespace ThinCam.Demo.ViewModels;

public sealed record CameraDeviceItem(CameraDevice Device)
{
    public string DisplayName =>
        $"{Device.Name} ({Device.Position}){(Device.IsDefault ? " — default" : string.Empty)}";

    public override string ToString() => DisplayName;
}
