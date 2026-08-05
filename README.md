# ThinCam

Thin platform-native camera frame capture for .NET.

ThinCam provides one managed API over the camera frameworks already shipped by Windows, Linux, macOS, Android, and iOS. It returns pooled BGRA32 frames without bundling OpenCV, FFmpeg, GStreamer, MAUI, or AndroidX.

## Platform support

| Platform | Native backend | Managed target | Minimum |
|---|---|---|---:|
| Windows | Media Foundation asynchronous `IMFSourceReader` | `net8.0` | Windows 10 |
| Linux | V4L2 streaming I/O | `net8.0` | Modern Linux with V4L2 |
| macOS | AVFoundation | `net8.0` | macOS 12 |
| Android | Camera2 NDK and `AImageReader` | `net10.0-android` | Android API 24 |
| iOS | AVFoundation | `net10.0-ios` | iOS 15 |

ThinCam produces top-to-bottom BGRA32 frames and exposes capability-driven Exposure, Focus, Zoom, and Light controls. Native camera buffers are copied once into pooled managed memory and delivered through a bounded, latest-frame asynchronous queue.

## Documentation

- [Architecture and implementation](Docs/Architecture.md) — design goals, data flow, managed abstraction, native ABI, every platform backend, threading, ownership, packaging, limitations, and extension strategy.
- [Building](Docs/Building.md) — exact native and `.csproj` build commands, prerequisites, runtime asset staging, mobile workloads, packaging, verification, CI-equivalent commands, and troubleshooting.
- [Public API](Docs/Api.md) — permissions, device enumeration, camera lifecycle, frame ownership, controls, errors, and usage patterns.
- [Camera controls](Docs/Controls.md) — capability discovery and the Exposure, Focus, Zoom, and Light implementations on every platform.
- [SkiaSharp adapter](Docs/SkiaSharp.md) — stride-aware bitmap conversion, rotation and mirroring, encoding, reusable preview buffers, and tests.
- [Avalonia integration](Docs/Avalonia.md) — reusable preview control/source, threading, transforms, desktop/mobile demo heads, permissions, and build/run instructions.
- [Native ABI](Docs/Abi.md) — binary contract, controls, and callback lifetime rules.

## Basic use

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
        ReadOnlyMemory<byte> bgra = frame.Data;
        Console.WriteLine(
            $"{frame.Width}x{frame.Height}, " +
            $"stride={frame.Stride}, bytes={frame.DataLength}");
    }
}
```

A `Camera` supports one asynchronous frame consumer. When the consumer falls behind, ThinCam disposes the oldest queued frame and retains newer frames. Every received `VideoFrame` must be disposed.


## Camera controls

Controls are capability-driven and available from an open camera:

```csharp
CameraCapabilities capabilities = await camera.GetCapabilitiesAsync();

if (capabilities.Exposure.CompensationEv is { } compensation)
{
    await camera.Controls.Exposure.SetCompensationAsync(
        Math.Clamp(0.5, compensation.Minimum, compensation.Maximum));
}

if (capabilities.Focus.Modes.Contains(FocusMode.ContinuousAuto))
{
    await camera.Controls.Focus.SetModeAsync(FocusMode.ContinuousAuto);
}

if (capabilities.Zoom.Factor is { } zoom)
{
    await camera.Controls.Zoom.SetFactorAsync(
        Math.Min(2.0, zoom.Maximum));
}

if (capabilities.Light.IsAvailable)
{
    await camera.Controls.Light.SetEnabledAsync(true);
}
```

Every control is optional. ThinCam exposes actual device ranges and supported modes and returns `NotSupported` when a driver or camera does not provide a requested operation. See [Camera controls](Docs/Controls.md) for semantics and platform mappings.

## Optional SkiaSharp adapter

`ThinCam.SkiaSharp` converts raw camera frames into Skia-owned images without adding a graphics dependency to the capture core:

```csharp
using SkiaSharp;
using ThinCam.SkiaSharp;

using SKBitmap bitmap = frame.ToSKBitmap(
    SkiaFrameTransform.Presentation);

using SKImage image = frame.ToSKImage(
    SkiaFrameTransform.Presentation);

byte[] jpeg = frame.EncodeToBytes(
    SKEncodedImageFormat.Jpeg,
    quality: 90,
    SkiaFrameTransform.Presentation);
```

For live preview, `SkiaFrameBuffer` reuses two bitmaps and synchronizes capture updates with render reads:

```csharp
using var preview = new SkiaFrameBuffer();
preview.Update(frame, SkiaFrameTransform.Presentation);
preview.TryDraw(canvas, destination);
```

The adapter respects source stride, keeps alpha opaque, applies optional rotation and mirroring metadata, and supports Skia image encoding. See [SkiaSharp adapter](Docs/SkiaSharp.md).

## Optional Avalonia preview control

`ThinCam.Avalonia` provides a reusable custom-rendered preview control without
moving camera ownership into the UI layer:

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

The source uses the reusable Skia double buffer, accepts frames from worker
threads, and the control coalesces redraws onto Avalonia's UI dispatcher. The
repository includes a full desktop, Android, and iOS diagnostic demo using this
control. See [Avalonia integration](Docs/Avalonia.md).

## Android permission request

The host app must declare:

```xml
<uses-permission android:name="android.permission.CAMERA" />
```

Request permission from an `Activity`:

```csharp
CameraPermissionStatus permission =
    await CameraPermissions.RequestAsync(this);
```

The parameterless request returns `HostActionRequired` on Android unless permission is already granted.

## Apple permission configuration

Add a purpose string to the host application's `Info.plist`:

```xml
<key>NSCameraUsageDescription</key>
<string>This app uses the camera to process live video frames.</string>
```

Then request access with:

```csharp
CameraPermissionStatus permission =
    await CameraPermissions.RequestAsync();
```

A sandboxed macOS application may also require a camera entitlement.

## Quick build

Native libraries must be built and staged before building or packaging the managed library.

### Linux

```bash
./Build/build-linux.sh

dotnet build Sources/ThinCam/ThinCam.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net8.0 \
  -f net8.0

dotnet build Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net8.0 \
  -f net8.0

dotnet build Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net8.0 \
  -f net8.0

LD_LIBRARY_PATH="$PWD/Build/Native/runtimes/linux-x64/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  dotnet run \
    --project Samples/ThinCamDemo.Console/ThinCamDemo.Console.csproj \
    -c Release
```

### Windows

From a Visual Studio Developer PowerShell:

```powershell
./Build/build-windows.ps1

dotnet build Sources/ThinCam/ThinCam.csproj `
  -c Release `
  -p:ThinCamTargetFrameworks=net8.0 `
  -f net8.0

dotnet build Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj `
  -c Release `
  -p:ThinCamTargetFrameworks=net8.0 `
  -f net8.0

dotnet build Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj `
  -c Release `
  -p:ThinCamTargetFrameworks=net8.0 `
  -f net8.0
```

Set `$env:RID = "win-arm64"` before the native script for ARM64.

### Apple native libraries and iOS managed target

On macOS with Xcode, CMake, and Ninja:

```bash
./Build/build-macos.sh
dotnet workload install ios

dotnet build Sources/ThinCam/ThinCam.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -f net10.0-ios \
  -r iossimulator-arm64

dotnet build Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -f net10.0-ios \
  -r iossimulator-arm64

dotnet build Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -f net10.0-ios \
  -r iossimulator-arm64
```

macOS desktop uses the `net8.0` ThinCam and ThinCam.SkiaSharp build commands shown for Linux/Windows after the dylib is staged.

### Android native libraries and managed target

```bash
export ANDROID_NDK_HOME="$ANDROID_SDK_ROOT/ndk/27.3.13750724"
./Build/build-macos.sh
dotnet workload install android

dotnet build Sources/ThinCam/ThinCam.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-android \
  -f net10.0-android

dotnet build Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-android \
  -f net10.0-android

dotnet build Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-android \
  -f net10.0-android
```

See [Docs/Building.md](Docs/Building.md) for complete prerequisites, direct CMake commands, all runtime identifiers, package validation, and troubleshooting.

## Repository layout

```text
Sources/ThinCam/              Managed API, P/Invoke, and runtime assets
Sources/ThinCam.SkiaSharp/    Optional SkiaSharp conversion and preview helpers
Sources/ThinCam.Avalonia/     Reusable Avalonia preview source and control
Native/include/           Stable versioned C ABI
Native/common/            Shared status and pixel conversion code
Native/windows/           Media Foundation backend
Native/linux/             V4L2 backend and conversion tests
Native/apple/             macOS/iOS AVFoundation backend
Native/android/           Android Camera2 NDK backend
Samples/ThinCamDemo.Console/  Desktop frame-reader sample
Samples/ThinCamDemo*/    Shared Avalonia demo and desktop/Android/iOS heads
Tests/ThinCamTests.SkiaSharp/  Stride, transform, encoding, and buffer tests
Tests/ThinCamTests.Avalonia/   Preview-source ownership and transform tests
Build/                    Native build, staging, and pack scripts
Docs/                     Architecture, API, build, and ABI docs
```

## Version 2 boundaries

- BGRA32 output only.
- One managed frame consumer per camera.
- One pooled managed-memory copy per delivered frame.
- No audio, recording, core image encoding, still-photo flash sequencing, or GPU-native camera surfaces. Optional encoding lives in `ThinCam.SkiaSharp`; the optional reusable preview control lives in `ThinCam.Avalonia`.
- Linux supports YUYV/UYVY and rejects MJPEG-only devices.
- Rotation and mirroring are metadata; pixels are not transformed.
- Requested dimensions and frame rate are preferences, not guarantees.

## Status

The repository contains the capture core, ABI v2 camera controls, SkiaSharp adapter, reusable Avalonia control, and shared Avalonia demo with desktop/Android/iOS heads. Linux native compilation, conversion tests, ABI checks, project/XML/YAML validation, and documentation checks were completed in the implementation environment. Managed builds, Windows/Apple/Android native builds, platform permission dialogs, and physical camera behavior still require their platform toolchains and hardware validation before a production release.

## License

MIT
