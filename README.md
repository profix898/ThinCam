# ThinCam

[![Nuget](https://img.shields.io/nuget/v/ThinCam?style=flat-square&logo=nuget&color=blue)](https://www.nuget.org/packages/ThinCam)

Thin platform-native camera frame capture for .NET.

ThinCam provides one managed API over the camera frameworks already shipped by Windows, Linux, macOS, Android, and iOS. It returns pooled BGRA32 frames without bundling OpenCV, FFmpeg, GStreamer, MAUI, or AndroidX.

## Platform support

| Platform | Native backend                                  | Managed target    | Minimum                |
| -------- | ----------------------------------------------- | ----------------- | ----------------------:|
| Windows  | Media Foundation asynchronous `IMFSourceReader` | `net10.0`         | Windows 10             |
| Linux    | V4L2 streaming I/O                              | `net10.0`         | Modern Linux with V4L2 |
| macOS    | AVFoundation                                    | `net10.0`         | macOS 12               |
| Android  | Camera2 NDK and `AImageReader`                  | `net10.0-android` | Android API 24         |
| iOS      | AVFoundation                                    | `net10.0-ios`     | iOS 15                 |

ThinCam produces top-to-bottom BGRA32 frames and exposes capability-driven Exposure, Focus, Zoom, and Light controls. Native camera buffers are copied once into pooled managed memory and delivered through a bounded, latest-frame asynchronous queue.

## Basic use

```csharp
using ThinCam;

if (CameraPermissions.GetStatus() != CameraPermissionStatus.Granted)
    await CameraPermissions.RequestAsync();

var cameraDevice = CameraDevices.Default ?? throw new InvalidOperationException("No camera was found.");
await using Camera camera = await Camera.OpenAsync(cameraDevice, new CameraOpenOptions { Width = 1280, Height = 720, FramesPerSecond = 30 });

await foreach (VideoFrame frame in camera.GetFramesAsync())
{
    using (frame)
    {
        ReadOnlyMemory<byte> bgra = frame.Data;
        Console.WriteLine($"{frame.Width}x{frame.Height}, stride={frame.Stride}, bytes={frame.DataLength}");
    }
}
```

A `Camera` supports one asynchronous frame consumer. When the consumer falls behind, ThinCam disposes the oldest queued frame and retains newer frames, favoring low latency over completeness. Every received `VideoFrame` must be disposed; `frame.Data` is valid only until disposal.

## Camera controls

Controls are capability-driven and available from an open camera:

```csharp
CameraCapabilities capabilities = await camera.GetCapabilitiesAsync();

if (capabilities.Exposure.CompensationEv is { } compensation)
    await camera.Controls.Exposure.SetCompensationAsync(Math.Clamp(0.5, compensation.Minimum, compensation.Maximum));

if (capabilities.Focus.Modes.Contains(FocusMode.ContinuousAuto))
    await camera.Controls.Focus.SetModeAsync(FocusMode.ContinuousAuto);

if (capabilities.Zoom.Factor is { } zoom)
    await camera.Controls.Zoom.SetFactorAsync(Math.Min(2.0, zoom.Maximum));
```

Every control is optional. ThinCam exposes the actual device ranges and supported modes — including a camera Light where the device provides one — and reports `NotSupported` when a driver or camera does not provide a requested operation.

## Optional: SkiaSharp adapter

`ThinCam.SkiaSharp` converts raw camera frames into Skia-owned images without adding a graphics dependency to the capture core. It respects source stride, keeps alpha opaque, applies optional rotation and mirroring metadata, and supports Skia image encoding:

```csharp
using SkiaSharp;
using ThinCam.SkiaSharp;

using SKBitmap bitmap = frame.ToSKBitmap(SkiaFrameTransform.Presentation);

byte[] jpeg = frame.EncodeToBytes(SKEncodedImageFormat.Jpeg, quality: 90, SkiaFrameTransform.Presentation);
```

For live preview, `SkiaFrameBuffer` reuses two bitmaps and synchronizes capture updates with render reads:

```csharp
using var preview = new SkiaFrameBuffer();
preview.Update(frame, SkiaFrameTransform.Presentation);
preview.TryDraw(canvas, destination);
```

## Optional: Avalonia preview control

`ThinCam.Avalonia` provides a reusable, custom-rendered preview control without moving camera ownership into the UI layer. A `CameraPreviewSource` accepts frames from a capture worker thread, and the control coalesces redraws onto Avalonia's UI dispatcher:

```csharp
using ThinCam.Avalonia;

public CameraPreviewSource PreviewSource { get; } = new();

await foreach (VideoFrame frame in camera.GetFramesAsync(token))
{
    using (frame)
    {
        PreviewSource.Publish(frame);
    }
}
```

```xml
<tc:CameraPreview Source="{Binding PreviewSource}"
                  Stretch="Uniform"
                  PlaceholderText="Waiting for camera…" />
```

The repository includes a full desktop, Android, and iOS diagnostic demo built on this control.

## Permissions

**Android** — declare the camera permission in the manifest and request it from an `Activity`; the parameterless `CameraPermissions.RequestAsync()` returns `HostActionRequired` unless permission is already granted:

```xml
<uses-permission android:name="android.permission.CAMERA" />
```

```csharp
CameraPermissionStatus permission = await CameraPermissions.RequestAsync(activity);
```

**iOS and macOS** — add a camera purpose string to the host application's `Info.plist`, then call the parameterless `CameraPermissions.RequestAsync()`. A sandboxed macOS application may also require a camera entitlement:

```xml
<key>NSCameraUsageDescription</key>
<string>This app uses the camera to process live video frames.</string>
```

**Windows and Linux** — there is no in-app permission prompt. Windows applies the OS camera privacy settings, and Linux requires read/write access to the `/dev/video*` device nodes.

## Boundaries (in Version 2)

- BGRA32 output only.
- One managed frame consumer per camera.
- One pooled managed-memory copy per delivered frame.
- No audio, recording, core image encoding, still-photo flash sequencing, or GPU-native camera surfaces. Optional encoding lives in `ThinCam.SkiaSharp`; the optional reusable preview control lives in `ThinCam.Avalonia`.
- Linux supports YUYV/UYVY and rejects MJPEG-only devices.
- Rotation and mirroring are metadata; pixels are not transformed.
- Requested dimensions and frame rate are preferences, not guarantees.

## Status

The repository contains the capture core, ABI v2 camera controls, SkiaSharp adapter, reusable Avalonia control, and shared Avalonia demo with desktop/Android/iOS heads. Linux native compilation, conversion tests, ABI checks, project/XML/YAML validation, and documentation checks were completed in the implementation environment. Managed builds, Windows/Apple/Android native builds, platform permission dialogs, and physical camera behavior still require their platform toolchains and hardware validation before a production release.

## More documentation

Detailed guides live in the [`Docs`](https://github.com/profix898/ThinCam/tree/main/Docs) folder of the source repository:

- [Architecture and implementation](https://github.com/profix898/ThinCam/blob/main/Docs/Architecture.md)
- [Building and packaging](https://github.com/profix898/ThinCam/blob/main/Docs/Building.md)
- [Public API](https://github.com/profix898/ThinCam/blob/main/Docs/API.md)
- [Camera controls](https://github.com/profix898/ThinCam/blob/main/Docs/Controls.md)
- [SkiaSharp adapter](https://github.com/profix898/ThinCam/blob/main/Docs/SkiaSharp.md)
- [Avalonia integration and demo](https://github.com/profix898/ThinCam/blob/main/Docs/Avalonia.md)
- [Native ABI](https://github.com/profix898/ThinCam/blob/main/Docs/ABI.md)

## License

ThinCam is licensed under the terms of the MIT license (<http://opensource.org/licenses/MIT>, see LICENSE.txt).
