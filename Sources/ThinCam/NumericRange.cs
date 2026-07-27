namespace ThinCam;

public sealed record NumericRange<T>(
    T Minimum,
    T Maximum,
    T Default,
    T Step);
