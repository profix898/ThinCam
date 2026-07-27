# ThinCam camera controls

ThinCam exposes a small, capability-driven control API for **Exposure**, **Focus**, **Zoom**, and **Light**. Controls are available only after a camera is open because many drivers report capabilities only for an active capture device.

The public API deliberately uses the word **Light**. Platform implementations may map that feature to native APIs whose identifiers use other terminology, but those names do not appear in ThinCam's public surface.

## Design rules

1. Query capabilities before presenting controls in an application UI.
2. Treat every control and mode as optional.
3. Use the ranges and steps reported by the selected device; do not hard-code webcam assumptions.
4. Out-of-range values are rejected rather than silently clamped. In-range values can be quantized to the nearest device step when the platform exposes a discrete control.
5. Control calls are serialized with camera disposal and with other control calls.
6. A successful control call means the platform API accepted the value. Hardware may still converge asynchronously, particularly for automatic exposure and focus.
7. Control values are current camera settings, not authoritative per-frame metadata.

## Discovering capabilities

```csharp
CameraCapabilities capabilities =
    await camera.GetCapabilitiesAsync(cancellationToken);
```

Capabilities are cached for the lifetime of the open `Camera`. Open a new camera instance after changing devices or reconnecting hardware.

```csharp
if (capabilities.Exposure.CompensationEv is { } compensation)
{
    Console.WriteLine(
        $"Exposure compensation: {compensation.Minimum} to " +
        $"{compensation.Maximum} EV, step {compensation.Step}");
}

if (capabilities.Focus.ManualPosition is not null)
{
    Console.WriteLine("Manual focus is available.");
}

if (capabilities.Zoom.Factor is { } zoom)
{
    Console.WriteLine($"Zoom: {zoom.Minimum}x to {zoom.Maximum}x");
}

if (capabilities.Light.IsAvailable)
{
    Console.WriteLine("A continuous camera light is available.");
}
```

`NumericRange<T>` contains `Minimum`, `Maximum`, `Default`, and `Step`. A zero step means that the backend cannot express a reliable discrete increment. The application should still stay within the reported minimum and maximum.

## Exposure

### Modes

```csharp
public enum ExposureMode
{
    Auto = 1,
    Manual = 2,
    Locked = 3
}
```

- `Auto` lets the camera continuously or automatically select exposure.
- `Manual` uses explicit exposure duration and, where supported, ISO sensitivity.
- `Locked` freezes the current automatic value when the platform can do so reliably.

A device can expose any subset of these modes:

```csharp
if (capabilities.Exposure.Modes.Contains(ExposureMode.Locked))
{
    await camera.Controls.Exposure.SetModeAsync(ExposureMode.Locked);
}
```

### Exposure compensation

Exposure compensation is expressed in exposure-value units:

```csharp
double requestedEv = 0.5;
NumericRange<double>? range = capabilities.Exposure.CompensationEv;

if (range is not null &&
    requestedEv >= range.Minimum &&
    requestedEv <= range.Maximum)
{
    await camera.Controls.Exposure.SetCompensationAsync(requestedEv);
}
```

Prefer an exact supported step when the range reports a nonzero `Step`. Some platform APIs accept a continuous request and then quantize it to the nearest device-supported value; read the state back when exact confirmation matters.

### Manual duration and ISO

```csharp
if (capabilities.Exposure.Duration is { } duration)
{
    TimeSpan requested = TimeSpan.FromMilliseconds(10);

    if (requested >= duration.Minimum && requested <= duration.Maximum)
    {
        double? iso = capabilities.Exposure.Iso is { } isoRange
            ? Math.Clamp(200.0, isoRange.Minimum, isoRange.Maximum)
            : null;

        await camera.Controls.Exposure.SetManualAsync(requested, iso);
    }
}
```

Manual duration is represented as `TimeSpan` in managed code and integer microseconds in the native ABI. The minimum public duration is one microsecond.

A long exposure can reduce the achievable frame rate. ThinCam does not currently renegotiate or report a changed frame rate after a control operation.

### Reading exposure state

```csharp
ExposureState state = await camera.Controls.Exposure.GetStateAsync();

Console.WriteLine(
    $"mode={state.Mode}, compensation={state.CompensationEv}, " +
    $"duration={state.Duration}, ISO={state.Iso}");
```

Unsupported fields are `null`.

## Focus

### Modes

```csharp
public enum FocusMode
{
    Auto = 1,
    ContinuousAuto = 2,
    Manual = 3,
    Locked = 4
}
```

- `Auto` requests a one-shot automatic focus operation where the backend supports it.
- `ContinuousAuto` keeps automatic focus running.
- `Manual` uses a normalized lens position.
- `Locked` freezes the current focus where the platform can provide that behavior.

The distinction between one-shot and continuous automatic focus is not available on every desktop driver. ThinCam advertises only the modes it can map without pretending they are equivalent.

### Manual position

Manual focus position is normalized:

```text
0.0 = nearest supported focus
1.0 = farthest supported focus
```

```csharp
if (capabilities.Focus.ManualPosition is not null)
{
    await camera.Controls.Focus.SetPositionAsync(0.7);
}
```

`SetPositionAsync` selects manual focus before applying the requested position.

The value is device-relative. It is not a distance in meters and should not be compared across cameras.

### Reading focus state

```csharp
FocusState state = await camera.Controls.Focus.GetStateAsync();
Console.WriteLine($"mode={state.Mode}, position={state.Position}");
```

The reported position is the last/current setting visible through the platform API. It is not guaranteed to be the final physical lens position for an in-progress automatic focus operation.

## Zoom

Zoom is expressed as a factor:

```text
1.0 = no zoom relative to the backend's default field of view
2.0 = approximately two-times zoom
```

```csharp
if (capabilities.Zoom.Factor is { } zoom)
{
    double factor = Math.Min(2.0, zoom.Maximum);
    await camera.Controls.Zoom.SetFactorAsync(factor);
}
```

Read the current factor with:

```csharp
double factor = await camera.Controls.Zoom.GetFactorAsync();
```

ThinCam does not classify zoom as optical or digital. It may be implemented by a physical lens, sensor crop, driver transform, or platform camera pipeline.

## Light

`CameraControls.Light` controls a continuous illumination source associated with the selected camera. It does not control a still-photo flash sequence, screen illumination, or a privacy indicator.

### Enable or disable

```csharp
if (capabilities.Light.IsAvailable)
{
    await camera.Controls.Light.SetEnabledAsync(true);
}
```

### Variable level

Some devices expose only on/off control. Check `SupportsVariableLevel` and the `Level` range:

```csharp
if (capabilities.Light.SupportsVariableLevel &&
    capabilities.Light.Level is { } level)
{
    double requested = Math.Clamp(0.5, level.Minimum, level.Maximum);
    await camera.Controls.Light.SetLevelAsync(requested);
}
```

Light level is normalized by the backend. `1.0` is the maximum requested intensity. A value at the platform's minimum can be effectively dark or off on some hardware, so use `SetEnabledAsync(false)` when the intent is definitely to disable the light.

### Reading light state

```csharp
CameraLightState state = await camera.Controls.Light.GetStateAsync();
Console.WriteLine($"enabled={state.IsEnabled}, level={state.Level}");
```

`Level` is `null` for on/off-only devices.

## Error handling

An unsupported control throws `CameraException` with `CameraErrorCode.NotSupported`:

```csharp
try
{
    await camera.Controls.Zoom.SetFactorAsync(2.0);
}
catch (CameraException exception) when (
    exception.ErrorCode == CameraErrorCode.NotSupported)
{
    // Hide or disable the control in the application UI.
}
```

Values rejected by managed guard clauses, such as `NaN`, negative zoom, or a focus position outside `0.0` to `1.0`, throw `ArgumentOutOfRangeException`. A value that passes those basic checks but cannot be represented by the device range or step is rejected by the native backend as `CameraException` with `CameraErrorCode.InvalidArgument`. Native device or driver failures use another stable `CameraErrorCode` such as `DeviceNotFound`, `DeviceBusy`, or `Platform`.

The preferred application pattern is to validate against `CameraCapabilities` before calling a setter and still handle `CameraException`, because a device can disappear or change state between capability discovery and the operation.

## Threading and lifecycle

Managed control operations use a per-camera semaphore. This guarantees that:

- Two control changes do not enter the native backend concurrently.
- Camera disposal waits for an active control operation.
- A control operation started after disposal fails with `ObjectDisposedException`.

Each native backend then uses its own required serialization mechanism:

- Windows serializes access through the camera's control mutex.
- Linux serializes V4L2 control `ioctl` calls through the control mutex.
- Apple acquires the AVFoundation device configuration lock.
- Android updates and resubmits the active Camera2 repeating request under the lifecycle mutex.

Frame delivery continues while controls are changed. A backend must not call application code while holding a native control lock.

## Platform implementation matrix

| Capability | Windows | Linux | macOS | iOS | Android |
|---|---|---|---|---|---|
| Exposure mode | Driver `IAMCameraControl` | V4L2 exposure controls | AVFoundation exposure modes (no manual) | AVFoundation exposure modes | Camera2 AE mode/request |
| Compensation | Not exposed by the current Windows backend | V4L2 auto-exposure bias | Not exposed by AVFoundation on macOS | AVFoundation target bias | Camera2 AE compensation |
| Manual duration | Driver logarithmic exposure value | V4L2 absolute exposure | Not exposed by AVFoundation on macOS | AVFoundation custom duration | Camera2 sensor exposure time |
| ISO | Not exposed by the current Windows backend | V4L2 ISO sensitivity | Not exposed by AVFoundation on macOS | AVFoundation custom ISO | Camera2 sensor sensitivity |
| Focus | Driver `IAMCameraControl` | V4L2 focus controls | AVFoundation focus modes | AVFoundation focus modes/lens position | Camera2 AF and lens distance |
| Zoom | Driver `IAMCameraControl` | V4L2 absolute zoom | Not exposed by AVFoundation on macOS | AVFoundation video zoom factor | Camera2 crop region |
| Light | Extended camera control when available | V4L2 continuous LED mode | AVFoundation device light | AVFoundation device light | Camera2 flash mode |
| Variable light level | Windows adjustable-power extended control | V4L2 light intensity | AVFoundation level | AVFoundation level | Not exposed by the current Android backend |

### Windows notes

Legacy webcam exposure values are logarithmic base-two seconds. ThinCam converts them to and from `TimeSpan`. Exposure compensation and ISO are not advertised unless a future backend adds a reliable extended-property implementation.

The variable Light control uses the Windows extended-camera adjustable-power payload when the driver exposes it. On/off Light remains optional and driver-dependent.

### Linux notes

V4L2 controls are optional and driver-defined. ThinCam queries the actual minimum, maximum, step, default, menu values, and read/write flags. Exposure duration is converted from V4L2's 100-microsecond units.

### Apple notes

Configuration changes require an AVFoundation device lock. Automatic operations can converge after the setter returns. The platform light level uses a 0-to-1 value and can report a thermally reduced maximum.

AVFoundation only exposes exposure target bias, custom exposure duration and ISO, locked lens position, and video zoom factor on iOS. The shared Apple backend compiles those code paths for iOS only, so on macOS the compensation, manual duration, ISO, manual focus distance, and zoom controls report as unsupported. Exposure mode, focus mode, and the Light controls work on both operating systems.

### Android notes

Exposure, focus, zoom, and Light changes modify the active Camera2 request and resubmit it. One-shot autofocus uses a single capture request with an autofocus trigger, then restores the repeating request. Android Light is currently on/off only.

## Native ABI

Control support is ABI version 2. Every backend exports:

```c
tc_status tc_camera_get_control_info(
    tc_camera* camera,
    tc_control_id id,
    tc_control_info* info);

tc_status tc_camera_get_control(
    tc_camera* camera,
    tc_control_id id,
    tc_control_value* value);

tc_status tc_camera_set_control(
    tc_camera* camera,
    const tc_control_value* value);
```

The ABI uses generic typed values so new controls can be added without adding a function per property. See [ABI.md](ABI.md) for binary layout and versioning rules.

## Current limitations

- Capability changes are not pushed as events.
- Controls cannot be configured before `Camera.OpenAsync` completes.
- Multi-control operations are serialized but are not transactionally atomic across the native driver.
- Per-frame exposure, ISO, focus, zoom, and Light metadata are not included in `VideoFrame`.
- Windows exposure compensation and ISO are not currently implemented.
- Android variable Light level is not currently implemented.
- Hardware testing remains necessary across representative camera models and drivers.
