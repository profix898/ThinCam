namespace ThinCam;

public sealed record ExposureState(
    ExposureMode? Mode,
    double? CompensationEv,
    TimeSpan? Duration,
    double? Iso);

public sealed record FocusState(
    FocusMode? Mode,
    double? Position);

public sealed record CameraLightState(
    bool? IsEnabled,
    double? Level);
