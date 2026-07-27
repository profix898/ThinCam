# ThinCam public API guide

This guide describes the managed API exposed by `Sources/ThinCam`. For implementation details, see [ARCHITECTURE.md](ARCHITECTURE.md). For native and managed build instructions, see [BUILDING.md](BUILDING.md).

## Contents

- [Minimal capture example](#1-minimal-capture-example)
- [Required application configuration](#2-required-application-configuration)
- [Permissions](#3-permissions)
- [Device enumeration](#4-device-enumeration)
- [Opening a camera](#5-opening-a-camera)
- [Reading frames](#6-reading-frames)
- [`VideoFrame`](#7-videoframe)
- [Actual format and runtime errors](#8-actual-format-and-runtime-errors)
- [Error handling](#9-error-handling)
- [Disposal](#10-disposal)
- [UI integration](#11-ui-integration-pattern)
- [Processing fan-out](#12-processing-fan-out)
- [Current scope](#13-current-scope)

## 1. Minimal capture example

```csharp
using ThinCam;

CameraPermissionStatus permission = CameraPermissions.GetStatus();
if (permission != CameraPermissionStatus.Granted)
{
    permission = await CameraPermissions.RequestAsync();
}

if (permission != CameraPermissionStatus.Granted)
{
    throw new InvalidOperationException($"Camera permission is {permission}.");
}

CameraDevice device = CameraDevices.Default
    ?? throw new InvalidOperationException("No camera was found.");

await using Camera camera = await Camera.OpenAsync(
    device,
    new CameraOpenOptions
    {
        Width = 1280,
        Height = 720,
        FramesPerSecond = 30,
        QueueCapacity = 2
    });

await foreach (VideoFrame frame in camera.GetFramesAsync())
{
    using (frame)
    {
        ReadOnlySpan<byte> data = frame.Data.Span;
        Console.WriteLine(
            $"{frame.Width}x{frame.Height}, " +
            $"stride={frame.Stride}, bytes={frame.DataLength}");
    }
}
```

On Android, use the `Activity` overload when permission is not granted:

```csharp
CameraPermissionStatus permission =
    await CameraPermissions.RequestAsync(this);
```

## 2. Required application configuration

### Android

Add to `AndroidManifest.xml`:

```xml
<uses-permission android:name="android.permission.CAMERA" />
```

### iOS and macOS

Add to `Info.plist`:

```xml
<key>NSCameraUsageDescription</key>
<string>This app uses the camera to process live video frames.</string>
```

Use a purpose string that accurately describes the host application's camera use. A sandboxed macOS app may also require a camera entitlement.

### Linux

The process must be able to open the selected `/dev/video*` node for reading and writing. This can involve a distribution-specific group, ACL, udev rule, container device mapping, or sandbox permission.

### Windows

Windows camera privacy settings apply. ThinCam cannot display a Windows desktop permission prompt; access denial is reported when Media Foundation activates the device.

## 3. Permissions

### `CameraPermissions.GetStatus()`

Returns the current best-effort permission state.

```csharp
CameraPermissionStatus status = CameraPermissions.GetStatus();
```

Possible values:

| Value | Meaning |
|---|---|
| `Unknown` | Status could not be determined |
| `NotDetermined` | The user has not made a choice yet |
| `Granted` | Camera access is available |
| `Denied` | Access was denied |
| `Restricted` | Access is restricted by policy or parental controls |
| `HostActionRequired` | The host UI must perform the request, currently Android without an `Activity` |

### `CameraPermissions.RequestAsync()`

On Apple platforms, this can display the operating-system camera prompt. On Linux and Windows, it returns the available status without a ThinCam-owned prompt.

On Android, the parameterless overload returns `HostActionRequired` unless permission is already granted.

### `CameraPermissions.RequestAsync(Activity)`

Available only when targeting Android. It displays the Android runtime permission request using the supplied activity.

Cancellation cancels the managed wait. It cannot necessarily retract a permission dialog already owned by the operating system.

## 4. Device enumeration

### Enumerating all devices

```csharp
IReadOnlyList<CameraDevice> devices = CameraDevices.Enumerate();

foreach (CameraDevice device in devices)
{
    Console.WriteLine(
        $"{device.Name}: id={device.Id}, " +
        $"position={device.Position}, default={device.IsDefault}");
}
```

Enumeration is synchronous because the native ABI invokes all device callbacks before returning.

### Default device

```csharp
CameraDevice? device = CameraDevices.Default;
```

The default is backend-defined. It is not guaranteed to match a user's preference stored by another application.

### `CameraDevice`

```csharp
public sealed record CameraDevice(
    string Id,
    string Name,
    CameraPosition Position,
    bool IsDefault);
```

Treat `Id` as opaque. Do not parse it or assume it remains valid forever.

`CameraPosition` can be:

- `Unspecified`
- `Front`
- `Back`
- `External`

Windows and Linux currently report generic cameras as external because the low-level enumeration paths do not classify front/back position reliably.

## 5. Opening a camera

```csharp
await using Camera camera = await Camera.OpenAsync(device);
```

Or supply options:

```csharp
await using Camera camera = await Camera.OpenAsync(
    device,
    new CameraOpenOptions
    {
        Width = 1920,
        Height = 1080,
        FramesPerSecond = 30,
        PixelFormat = PixelFormat.Bgra32,
        QueueCapacity = 2
    },
    cancellationToken);
```

`OpenAsync` both opens and starts the native capture pipeline. It returns only after startup succeeds or definitively fails.

The requested width, height, and FPS are preferences. The backend may select a different supported configuration.

### `CameraOpenOptions`

| Property | Default | Rules |
|---|---:|---|
| `Width` | 640 | Must be greater than zero |
| `Height` | 480 | Must be greater than zero |
| `FramesPerSecond` | 30 | Must be greater than zero |
| `PixelFormat` | `Bgra32` | BGRA32 is the only supported value in version 1 |
| `QueueCapacity` | 2 | Must be between 1 and 32 |

A smaller queue reduces latency. A larger queue can absorb brief processing stalls but retains more memory and older frames.

### Permission requirement

`Camera.OpenAsync` requires `CameraPermissions.GetStatus()` to return `Granted`. Request permission before opening.

### Startup cancellation

The cancellation token is checked before native startup begins. Once the platform startup operation is running, version 1 does not interrupt it. Native startup has platform-specific internal timeouts where needed.

## 6. Reading frames

```csharp
await foreach (VideoFrame frame in camera.GetFramesAsync(cancellationToken))
{
    using (frame)
    {
        Process(frame);
    }
}
```

One `Camera` supports one call to `GetFramesAsync`. Create your own fan-out if several application components require each frame.

When the queue is full, ThinCam disposes the oldest queued frame. It does not block the native camera callback waiting for the consumer.

### Stream completion

The frame stream completes normally when the camera is disposed. A fatal native error completes it with a `CameraException`.

A typical loop handles cancellation separately from capture failure:

```csharp
try
{
    await foreach (VideoFrame frame in camera.GetFramesAsync(token))
    {
        using (frame)
        {
            Process(frame);
        }
    }
}
catch (OperationCanceledException) when (token.IsCancellationRequested)
{
    // Expected application cancellation.
}
catch (CameraException exception)
{
    Console.Error.WriteLine(
        $"Camera failed: {exception.ErrorCode}: {exception.Message}");
}
```

## 7. `VideoFrame`

A frame contains:

| Property | Meaning |
|---|---|
| `Data` | Pooled managed bytes valid until disposal |
| `DataLength` | Number of valid bytes |
| `Width` | Visible pixel width |
| `Height` | Visible pixel height |
| `Stride` | Number of bytes between the start of adjacent rows |
| `PixelFormat` | Currently always `Bgra32` |
| `RotationDegrees` | Best-effort clockwise orientation metadata |
| `IsMirrored` | Front-camera presentation metadata |
| `Timestamp` | Backend timestamp represented as a `TimeSpan` |

### Disposal

Every frame must be disposed. The recommended pattern is `using (frame)` inside the asynchronous loop.

Do not retain `Data` after disposal. Copy the bytes if another component needs longer ownership.

### Reading rows correctly

```csharp
ReadOnlySpan<byte> bytes = frame.Data.Span;

for (int y = 0; y < frame.Height; y++)
{
    ReadOnlySpan<byte> row = bytes.Slice(
        y * frame.Stride,
        frame.Width * 4);

    for (int x = 0; x < frame.Width; x++)
    {
        int offset = x * 4;
        byte blue = row[offset];
        byte green = row[offset + 1];
        byte red = row[offset + 2];
        byte alpha = row[offset + 3];
    }
}
```

Do not assume `Stride == Width * 4`, particularly on Apple platforms.

### Rotation and mirroring

ThinCam does not transform frame bytes. Apply rotation or mirroring in the UI or processing layer.

Android currently reports sensor orientation. Apple and desktop backends currently report zero rotation. Android and Apple front-facing cameras report mirroring metadata.

### Timestamps

Use timestamps to order frames and measure elapsed time within a session. Do not assume that timestamps from different platforms, processes, or cameras share the same epoch.

## 8. Actual format and runtime errors

### `Camera.ActiveFormat`

```csharp
CameraFormat? format = camera.ActiveFormat;
```

This is `null` until the first valid frame arrives. It records the actual width, height, stride, and pixel format.

### `Camera.LastError`

```csharp
CameraException? error = camera.LastError;
```

This stores the most recent native error. Fatal errors also terminate the frame stream.

## 9. Error handling

`CameraException.ErrorCode` uses stable cross-platform values:

```csharp
catch (CameraException exception) when (
    exception.ErrorCode == CameraErrorCode.DeviceBusy)
{
    // Tell the user to close another camera application.
}
```

Common recovery guidance:

| Error | Typical response |
|---|---|
| `PermissionDenied` | Request permission or direct the user to OS settings |
| `DeviceNotFound` | Re-enumerate devices |
| `DeviceBusy` | Close another camera session and retry |
| `FormatNotSupported` | Request a more common resolution or choose another camera |
| `Timeout` | Retry after resetting the camera/application state |
| `Platform` | Log full details and re-enumerate or restart capture |

## 10. Disposal

Always dispose a `Camera`:

```csharp
await using Camera camera = await Camera.OpenAsync(device);
```

Disposal:

- Stops native capture.
- Waits for native callbacks to drain.
- Completes the frame stream.
- Disposes queued frames.
- Releases the native handle and managed callback state.

Disposal is idempotent.

## 11. UI integration pattern

ThinCam intentionally does not provide a preview control. A UI application generally:

1. Reads frames on a background async loop.
2. Copies or converts the newest frame into a UI-owned bitmap or texture.
3. Schedules only the presentation step on the UI thread.
4. Disposes the `VideoFrame` immediately after copying.

Avoid keeping a pooled ThinCam frame alive while waiting for a slow UI render pass.

## 12. Processing fan-out

Since one camera has one reader, applications needing multiple processors should fan out explicitly. Decide whether each downstream consumer needs every frame or only the newest frame.

For latest-frame processors, copy only when a processor is ready. For guaranteed delivery, copy into an application-owned queue and accept the additional latency and memory cost.

Do not pass the same disposable `VideoFrame` to multiple components unless ownership and final disposal are coordinated precisely.

## 13. Current scope

The public API does not yet include:

- Supported-format enumeration.
- Camera controls.
- Still-photo capture.
- Audio.
- Recording or encoding.
- Hot-plug events.
- UI preview components.
- Zero-copy GPU buffers.
- Multiple frame readers.

These are intentionally outside the version-one contract.
