using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace ThinCam.Avalonia;

/// <summary>
/// The default control themes for <see cref="CameraPreview" /> and <see cref="CameraControlsView" />.
/// </summary>
/// <remarks>
/// Add this to <c>Application.Styles</c> to make the ThinCam controls usable:
/// <code>
/// &lt;Application.Styles&gt;
///     &lt;FluentTheme /&gt;
///     &lt;tc:ThinCamTheme /&gt;
/// &lt;/Application.Styles&gt;
/// </code>
/// The themes follow the ambient <c>ThemeVariant</c>, so light and dark mode are supported out
/// of the box. Override the <c>CameraPreview…Brush</c> and <c>CameraControls…Brush</c> resource
/// keys in your own resources to restyle the controls without replacing their templates.
/// </remarks>
public class ThinCamTheme : Styles
{
    /// <summary>Initializes a new instance of the <see cref="ThinCamTheme" /> class.</summary>
    public ThinCamTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
