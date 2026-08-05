namespace ThinCam;

/// <summary>
/// Describes the bounds, default, and increment of a numeric setting.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Minimum">The minimum supported value.</param>
/// <param name="Maximum">The maximum supported value.</param>
/// <param name="Default">The default value.</param>
/// <param name="Step">The supported increment between values.</param>
public sealed record NumericRange<T>(T Minimum,
                                     T Maximum,
                                     T Default,
                                     T Step);
