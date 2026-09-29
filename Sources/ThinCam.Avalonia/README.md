# ThinCam.Avalonia

Reusable, lookless Avalonia controls for ThinCam.

The package contains four public pieces:

- `CameraPreviewSource` — a thread-safe, MVVM-friendly presentation source. A camera loop publishes `VideoFrame`
  instances into it from any thread.
- `CameraPreview` — a `TemplatedControl` that presents the latest source frame together with themable chrome and
  placeholder content.
- `CameraPreviewSurface` — the `Control` that performs the actual Skia draw. It is the `PART_Surface` of `CameraPreview`
  and can be used standalone or in a custom template.
- `CameraControlsView` — a `TemplatedControl` that presents and edits the exposure, focus, zoom, and light controls of a
  `Camera`.

Neither control opens, starts, stops, or owns a camera. This keeps capture lifecycle and permission handling in the
application while allowing the same controls to be reused in MVVM, code-behind, or custom composition architectures.

## Setup

Add the default control themes to your application styles:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:tc="using:ThinCam.Avalonia">
    <Application.Styles>
        <FluentTheme />
        <tc:ThinCamTheme />
    </Application.Styles>
</Application>
```

`ThinCamTheme` is equivalent to `<StyleInclude Source="avares://ThinCam.Avalonia/Themes/Default.axaml" />`.

## Preview

```xml
<tc:CameraPreview Source="{Binding PreviewSource}"
                  Stretch="Uniform"
                  CornerRadius="8"
                  PlaceholderContent="Open a camera" />
```

```csharp
public CameraPreviewSource PreviewSource { get; } = new();

await foreach (VideoFrame frame in camera.GetFramesAsync(token))
{
    using (frame)
    {
        PreviewSource.Publish(
            frame,
            SkiaFrameTransform.Presentation);
    }
}
```

`PlaceholderContent` accepts any content and is presented by `PART_Placeholder`; use `PlaceholderTemplate` for a custom
visual. Placeholder visibility is driven by the read-only `IsPlaceholderVisible` property
(`ShowPlaceholder && !HasFrame`), and the `:has-frame` pseudo-class is set while frames are being drawn.

## Controls

```xml
<tc:CameraControlsView Camera="{Binding ActiveCamera}"
                       ErrorOccurred="OnControlError"
                       StatusChanged="OnControlStatus" />
```

Setting `Camera` loads the camera capabilities and current control state. Slider and numeric edits are coalesced for
`ApplyDelay` (300 ms by default) before being sent to the camera; mode and light toggles are applied immediately. Every
apply raises either the `StatusChanged` or `ErrorOccurred` routed event.

The control reflects the camera and never writes to it on startup. Capabilities populate the mode pickers and the
numeric ranges, but they do not select a mode — that comes solely from the camera's reported state. If the camera does
not report a mode, the picker stays empty rather than showing a guess.

All state lives on the control itself — there is no view model and no dependency on `DataContext`. Capabilities the
camera does not report are exposed both as read-only `Has…` properties and as pseudo-classes:

| Property                  | Pseudo-class                 |
|---------------------------|------------------------------|
| `HasCamera`               | `:has-camera`                |
| `HasExposureModes`        | `:has-exposure-modes`        |
| `HasExposureCompensation` | `:has-exposure-compensation` |
| `HasManualExposure`       | `:has-manual-exposure`       |
| `HasIso`                  | `:has-iso`                   |
| `HasFocusModes`           | `:has-focus-modes`           |
| `HasManualFocus`          | `:has-manual-focus`          |
| `HasZoom`                 | `:has-zoom`                  |
| `HasLight`                | `:has-light`                 |
| `HasVariableLight`        | `:has-variable-light`        |
| `IsExposureManual`        | `:exposure-manual`           |
| `IsFocusManual`           | `:focus-manual`              |
| `HasError`                | `:has-error`                 |

### Ranges and errors

The camera's reported ranges are exposed as properties: `ManualExposureMinimum`/`Maximum`/`Step` (milliseconds),
`IsoMinimum`/`Maximum`/`Step`, `FocusPositionMinimum`/`Maximum`/`Step`, `LightLevelMinimum`/`Maximum`/`Step`, and
`ZoomMinimum`/`Maximum`/`Step`.

Sliders are bounded by these ranges, so they cannot express an invalid value. The numeric editors deliberately are
**not** — `NumericUpDown` clamps to `Minimum`/`Maximum` silently, which would hide an out-of-range entry rather than
report it. Instead `CameraControlsView` implements `INotifyDataErrorInfo` and validates against the camera's range as
soon as the value changes. An invalid entry:

- is never sent to the camera,
- marks the offending editor through Avalonia's standard `DataValidationErrors` adorner,
- sets `LastError`, `HasError`, and the `:has-error` pseudo-class.

Errors are shown per editor; the default template has no error banner. Bind `LastError`/`HasError`, or handle the
`ErrorOccurred` routed event, if you want a summary elsewhere.

Clearing an editor sets `NumericUpDown.Value` to null. The template routes the numeric bindings through
`NullToZeroConverter`, so an empty editor reads as zero rather than throwing, and then fails range validation like any
other invalid value.

Correcting the value clears all of it. If the camera rejects a value that passed validation, the same error surface is
used and the control re-reads the camera's real state so the editor stops showing a value the hardware never accepted.

## Theming

Both control themes follow the ambient `ThemeVariant`, so light and dark mode work out of the box. To restyle without
replacing a template, override these resource keys:

`CameraPreviewBackgroundBrush`, `CameraPreviewForegroundBrush`, `CameraPreviewBorderBrush`,
`CameraControlsSectionBackgroundBrush`, `CameraControlsSectionBorderBrush`, `CameraControlsForegroundBrush`,
`CameraControlsErrorBackgroundBrush`, `CameraControlsErrorBorderBrush`, `CameraControlsErrorForegroundBrush`.

To change the layout, supply your own `ControlTheme`. Template children bind to the templated parent, so a custom
template needs no view model:

```xml
<ControlTheme x:Key="{x:Type tc:CameraControlsView}" TargetType="tc:CameraControlsView">
    <Setter Property="Template">
        <ControlTemplate>
            <StackPanel IsVisible="{TemplateBinding HasZoom}">
                <Slider Minimum="{TemplateBinding ZoomMinimum}"
                        Maximum="{TemplateBinding ZoomMaximum}"
                        Value="{Binding ZoomFactor,
                                        RelativeSource={RelativeSource TemplatedParent},
                                        Mode=TwoWay}" />
            </StackPanel>
        </ControlTemplate>
    </Setter>
</ControlTheme>
```

Remember that `TemplateBinding` is one-way in Avalonia; editable values need an explicit
`RelativeSource TemplatedParent` binding with `Mode=TwoWay`.

The default `CameraPreview` template requires a `CameraPreviewSurface` named `PART_Surface` for live frames. The default
`CameraControlsView` template names its sections `PART_ExposureSection`, `PART_FocusSection`, `PART_ZoomSection`,
`PART_LightSection`, and `PART_EmptyPlaceholder`, so styles can target them directly:

```xml
<Style Selector="tc|CameraControlsView:not(:has-zoom) /template/ Border#PART_ZoomSection">
    <Setter Property="IsVisible" Value="False" />
</Style>
```
