namespace ThinCam;

public sealed record CameraCapabilities(
    ExposureCapabilities Exposure,
    FocusCapabilities Focus,
    ZoomCapabilities Zoom,
    CameraLightCapabilities Light);

public sealed record ExposureCapabilities(
    IReadOnlySet<ExposureMode> Modes,
    NumericRange<double>? CompensationEv,
    NumericRange<TimeSpan>? Duration,
    NumericRange<double>? Iso)
{
    public bool IsSupported =>
        Modes.Count != 0 || CompensationEv is not null || Duration is not null || Iso is not null;
}

public sealed record FocusCapabilities(
    IReadOnlySet<FocusMode> Modes,
    NumericRange<double>? ManualPosition)
{
    public bool IsSupported => Modes.Count != 0 || ManualPosition is not null;
}

public sealed record ZoomCapabilities(NumericRange<double>? Factor)
{
    public bool IsSupported => Factor is not null;
}

public sealed record CameraLightCapabilities(
    bool IsAvailable,
    bool SupportsVariableLevel,
    NumericRange<double>? Level);
