using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace ThinCam.Avalonia;

/// <summary>
/// Passes values through unchanged, but converts <see langword="null" /> back to the numeric
/// zero of the target type.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="global::Avalonia.Controls.NumericUpDown.Value" /> is nullable and becomes
/// <see langword="null" /> when the user clears the editor. Binding that straight to a
/// non-nullable property throws an <see cref="InvalidCastException" />, so the numeric editors
/// in the default <see cref="CameraControlsView" /> template route through this converter and
/// treat an empty editor as zero.
/// </para>
/// <para>
/// Zero is normally outside a camera's supported range, so clearing an editor surfaces the
/// usual validation error rather than silently doing nothing.
/// </para>
/// </remarks>
public sealed class NullToZeroConverter : IValueConverter
{
    /// <summary>Gets the shared converter instance.</summary>
    public static NullToZeroConverter Instance { get; } = new NullToZeroConverter();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not null)
            return value;

        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (type == typeof(decimal))
            return 0m;
        if (type == typeof(double))
            return 0d;
        if (type == typeof(float))
            return 0f;
        if (type == typeof(int))
            return 0;
        if (type == typeof(long))
            return 0L;

        return AvaloniaProperty.UnsetValue;
    }
}
