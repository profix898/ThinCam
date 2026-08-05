# ThinCam Avalonia integration and demo application

This guide describes the reusable `ThinCam.Avalonia` package and the cross-platform
Avalonia demo application that consumes it. The control library is intentionally
separate from both the raw capture core and the demo so application developers can
reuse the preview pipeline without copying sample-specific code.

For raw frame conversion and image encoding, see [SkiaSharp.md](SkiaSharp.md). For
camera lifecycle and controls, see [Api.md](Api.md) and [Controls.md](Controls.md).
For native and managed build prerequisites, see [Building.md](Building.md).

## Contents

- [Package boundaries](#1-package-boundaries)
- [Control architecture](#2-control-architecture)
- [Basic use](#3-basic-use)
- [`CameraPreviewSource`](#4-camerapreviewsource)
- [`CameraPreview`](#5-camerapreview)
- [Threading and ownership](#6-threading-and-ownership)
- [Transforms and snapshots](#7-transforms-and-snapshots)
- [Demo application](#8-demo-application)
- [Platform heads and permissions](#9-platform-heads-and-permissions)
- [Building and running](#10-building-and-running)
- [Tests](#11-tests)
- [Limitations and extension points](#12-limitations-and-extension-points)

## 1. Package boundaries

The repository now has three distinct presentation layers:

```text
ThinCam
    Raw BGRA32 capture, device enumeration, permissions, and camera controls

ThinCam.SkiaSharp
    Stride-aware conversion, transforms, encoding, and reusable Skia buffers

ThinCam.Avalonia
    Reusable Avalonia preview source and custom-rendered preview control
```

`ThinCam` remains free of graphics and UI dependencies. `ThinCam.SkiaSharp` is
usable in headless services, other UI frameworks, and image-processing pipelines.
`ThinCam.Avalonia` depends on both Avalonia's Skia rendering interface and
`ThinCam.SkiaSharp`.

The demo is not packaged as part of the reusable library:

```text
Samples/ThinCamDemo/          Shared views, view model, and services
Samples/ThinCamDemo.Desktop/  Windows, Linux, and macOS entry point
Samples/ThinCamDemo.Android/  Android entry point and Activity permission host
Samples/ThinCamDemo.iOS/      iOS entry point and Info.plist
```

There is deliberately no browser head because ThinCam does not currently expose a
WebAssembly/browser camera backend.

## 2. Control architecture

The reusable integration is split into two types:

```text
CameraPreviewSource
    Owns reusable Skia frame buffers
    Accepts frames from a capture worker thread
    Publishes frame-version notifications
    Does not reference a visual control

CameraPreview
    Avalonia Control
    Binds to a CameraPreviewSource
    Coalesces redraw requests onto the UI dispatcher
    Draws the latest bitmap through Avalonia's Skia lease
    Does not open, start, stop, or own a Camera
```

This split is useful for MVVM applications. A view model or capture service owns the
source, while a view binds the source to one or more visual controls. The capture
code does not need to find a control instance or call `Dispatcher.UIThread`.

The normal path for one frame is:

```text
ThinCam native callback
        ↓
VideoFrame in pooled managed memory
        ↓
view model calls CameraPreviewSource.Publish(frame)
        ↓
SkiaFrameBuffer copies into hidden reusable back bitmap
        ↓
front/back bitmap swap
        ↓
FrameChanged notification
        ↓
CameraPreview coalesces InvalidateVisual on UI dispatcher
        ↓
ICustomDrawOperation obtains Avalonia Skia canvas
        ↓
front bitmap is drawn with the requested Stretch behavior
```

## 3. Basic use

Reference the reusable package:

```xml
<ItemGroup>
  <PackageReference Include="ThinCam" Version="0.1.0" />
  <PackageReference Include="ThinCam.SkiaSharp" Version="0.1.0" />
  <PackageReference Include="ThinCam.Avalonia" Version="0.1.0" />
</ItemGroup>
```

When consuming the source projects directly:

```xml
<ItemGroup>
  <ProjectReference Include="../ThinCam/Sources/ThinCam/ThinCam.csproj" />
  <ProjectReference Include="../ThinCam/Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj" />
  <ProjectReference Include="../ThinCam/Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj" />
</ItemGroup>
```

Create and expose a preview source from a view model or capture service:

```csharp
using ThinCam.Avalonia;

public sealed class CameraViewModel : IAsyncDisposable
{
    private Camera? _camera;

    public CameraPreviewSource PreviewSource { get; } = new();

    public async Task StartAsync(
        CameraDevice device,
        CancellationToken cancellationToken)
    {
        _camera = await Camera.OpenAsync(device, cancellationToken: cancellationToken);

        await foreach (VideoFrame frame in
            _camera.GetFramesAsync(cancellationToken))
        {
            using (frame)
            {
                PreviewSource.Publish(frame);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_camera is not null)
        {
            await _camera.DisposeAsync();
        }

        PreviewSource.Dispose();
    }
}
```

Bind it in XAML:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:tc="using:ThinCam.Avalonia">
  <tc:CameraPreview Source="{Binding PreviewSource}"
                    Stretch="Uniform"
                    PlaceholderText="Waiting for camera…" />
</UserControl>
```

The default publish transform is `SkiaFrameTransform.Presentation`, which applies
ThinCam's rotation metadata and horizontal presentation mirroring.

## 4. `CameraPreviewSource`

`CameraPreviewSource` is a thread-safe presentation model backed by
`SkiaFrameBuffer`.

### 4.1 Publishing frames

```csharp
PreviewSource.Publish(
    frame,
    SkiaFrameTransform.Presentation);
```

`Publish` synchronously copies the frame into Skia-owned memory. The caller retains
ownership of `VideoFrame` and may dispose it immediately after `Publish` returns.
The preview source never retains `VideoFrame.Data` or native camera memory.

### 4.2 Source state

```csharp
bool hasFrame = PreviewSource.HasFrame;
long version = PreviewSource.Version;
```

`Version` advances whenever a frame is published or the source is cleared. It can
be used to detect whether a visual render represents a new camera frame.

### 4.3 Notifications

```csharp
PreviewSource.FrameChanged += (_, args) =>
{
    Console.WriteLine(
        $"version={args.Version}, " +
        $"size={args.PixelWidth}x{args.PixelHeight}, " +
        $"hasFrame={args.HasFrame}");
};
```

`FrameChanged` is raised on the thread that calls `Publish` or `Clear`. Handlers
must return quickly and must not assume they are running on the Avalonia UI thread.
The source isolates handler exceptions so a faulty UI subscriber cannot break the
camera capture loop.

### 4.4 Clearing and snapshots

```csharp
PreviewSource.Clear();

using SKBitmap? snapshot = PreviewSource.CopySnapshot();
```

`Clear` disposes both reusable bitmaps and tells bound controls to render their
placeholder. `CopySnapshot` returns an independent caller-owned `SKBitmap`; it may
be encoded or retained after newer preview frames arrive.

### 4.5 Disposal

Dispose the source after the capture loop and every visual that uses it have been
stopped:

```csharp
PreviewSource.Dispose();
```

Publishing, clearing, or taking a snapshot after disposal throws
`ObjectDisposedException`.

## 5. `CameraPreview`

`CameraPreview` is a lightweight Avalonia `Control`. It draws through an
`ICustomDrawOperation` and acquires the active `SKCanvas` from
`ISkiaSharpApiLeaseFeature`.

### 5.1 Properties

| Property | Type | Default | Purpose |
|---|---|---|---|
| `Source` | `CameraPreviewSource?` | `null` | Latest-frame source |
| `Stretch` | `Stretch` | `Uniform` | Scaling mode |
| `StretchDirection` | `StretchDirection` | `Both` | Whether the image may shrink or enlarge |
| `PreviewBackground` | `Color` | dark neutral | Background behind the frame |
| `PlaceholderForeground` | `Color` | light neutral | Placeholder text color |
| `PlaceholderText` | `string` | `No camera frame` | Text shown without a frame |
| `ShowPlaceholder` | `bool` | `true` | Enables placeholder drawing |

The frame is centered inside the control after scaling. `Uniform` preserves aspect
ratio and letterboxes; `UniformToFill` preserves aspect ratio and crops at the
control bounds; `Fill` allows independent X/Y scaling; `None` uses source pixels as
Avalonia drawing units.

### 5.2 Render-completion event

```csharp
preview.FrameRendered += (_, args) =>
{
    renderedFrameCounter++;
};
```

`FrameRendered` is posted to the Avalonia UI dispatcher and fires at most once per
published source version for each control. Resizing or repainting the same bitmap
does not count as another rendered camera frame.

### 5.3 Snapshot convenience

```csharp
using SKBitmap? snapshot = preview.CopySnapshot();
```

This forwards to the bound source. Applications that follow MVVM normally call
`CameraPreviewSource.CopySnapshot` from the view model instead.

## 6. Threading and ownership

### 6.1 Capture thread

`Camera.GetFramesAsync` may resume on worker-pool threads. Calling
`CameraPreviewSource.Publish` from that loop is supported. No UI dispatcher call is
required.

### 6.2 Double buffering

`SkiaFrameBuffer` maintains a front and a back `SKBitmap`:

1. The capture producer copies into the hidden back bitmap.
2. Existing renders continue reading the published front bitmap.
3. A short write lock swaps the two references.
4. The old front bitmap becomes the next reusable back bitmap.

The expensive pixel copy does not hold the front-buffer write lock. This prevents a
slow frame copy from blocking the renderer for the entire operation.

### 6.3 UI invalidation

A camera may publish more frames than the UI can draw. `CameraPreview` uses an
atomic pending flag so many `FrameChanged` events result in at most one queued
`InvalidateVisual` operation. The next render reads the newest published front
bitmap. The bitmap and its publication version are acquired under the same read lease,
so render statistics cannot attribute a bitmap to the wrong source version.

This is intentional latest-frame behavior. The preview is a real-time display, not
a lossless frame-processing consumer.

### 6.4 Disposal order

A safe application shutdown order is:

```text
cancel capture loop
        ↓
await capture loop completion
        ↓
dispose Camera
        ↓
clear or detach CameraPreview.Source
        ↓
dispose CameraPreviewSource
```

`CameraPreviewSource` and `SkiaFrameBuffer` coordinate active readers and writers so
Skia bitmaps are not released while an update or render callback is using them.

## 7. Transforms and snapshots

Transforms are applied when a frame is published, not during every draw:

```csharp
PreviewSource.Publish(frame, SkiaFrameTransform.None);
PreviewSource.Publish(frame, SkiaFrameTransform.ApplyRotation);
PreviewSource.Publish(frame, SkiaFrameTransform.ApplyMirroring);
PreviewSource.Publish(frame, SkiaFrameTransform.Presentation);
```

`Presentation` applies clockwise rotation first and horizontal presentation
mirroring second. For 90° and 270° rotation, the published bitmap width and height
are swapped.

Because the transformed pixels are stored in the reusable preview bitmap, changing
transform policy requires publishing a subsequent frame. `CameraPreview` itself
does not reinterpret rotation metadata.

Snapshot encoding can reuse SkiaSharp directly:

```csharp
using SKBitmap? bitmap = PreviewSource.CopySnapshot();
if (bitmap is not null)
{
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
    await File.WriteAllBytesAsync(path, png.ToArray(), cancellationToken);
}
```

## 8. Demo application

The demo is designed as a backend diagnostic application rather than only a camera
viewer.

### 8.1 Capture tab

The capture tab provides:

- Permission state and request action
- Refreshable device enumeration
- Device name, opaque identifier, position, and default selection
- Preferred width, height, frame rate, and ThinCam queue capacity
- Open, close, and restart operations
- Live preview through `ThinCam.Avalonia.CameraPreview`
- Negotiated dimensions, stride, and pixel format
- Received and rendered frame rates
- Total received/rendered frames and preview-skipped count
- Last-frame age
- PNG snapshot saving

### 8.2 Controls tab

The controls tab uses `Camera.GetCapabilitiesAsync` and enables only supported
features:

- Exposure mode
- Exposure compensation in EV
- Manual exposure duration and ISO
- Focus mode
- Normalized manual focus position
- Zoom factor
- Light enabled state and variable level when available

Unsupported controls remain visible but disabled, making driver and platform
differences obvious during testing.

### 8.3 Diagnostics tab

The diagnostics tab records:

- Platform, process architecture, and .NET runtime
- Permission transitions
- Device enumeration
- Camera open, stop, and restart events
- Active format
- Capability descriptors
- Control writes and readback
- Capture failures
- Frame statistics

The report can be exported through Avalonia's storage provider.

## 9. Platform heads and permissions

### 9.1 Desktop

`Samples/ThinCamDemo.Desktop` is used on Windows, Linux, and macOS. It references
`Avalonia.Desktop` and copies the staged ThinCam native library for the selected
SDK/runtime identifier into the output directory.

Windows camera privacy settings and Linux `/dev/video*` access still apply.

macOS requires an application bundle with `NSCameraUsageDescription`. The
repository's `Build/run-demo-macos.sh` publishes the desktop head, creates a minimal
`.app` bundle from `Samples/ThinCamDemo.Desktop/Info.plist`, signs it ad hoc, and
opens it so the system camera prompt has a valid purpose string.

### 9.2 Android

`Samples/ThinCamDemo.Android` declares:

```xml
<uses-permission android:name="android.permission.CAMERA" />
```

The Android `MainActivity` installs an `AndroidPlatformServices` instance before
Avalonia creates the shared view. That service passes the current `Activity` to:

```csharp
CameraPermissions.RequestAsync(activity, cancellationToken);
```

The shared view model therefore does not reference Android types.

### 9.3 iOS

`Samples/ThinCamDemo.iOS/Info.plist` contains:

```xml
<key>NSCameraUsageDescription</key>
<string>ThinCam Demo uses the camera to validate live frame capture and camera controls.</string>
```

The default shared platform service can call ThinCam's parameterless Apple
permission request. The iOS head uses `AvaloniaAppDelegate<App>` and the normal
single-view Avalonia application lifetime.

## 10. Building and running

Build the native backend before the corresponding app head. The commands below run
from the repository root.

### 10.1 Windows x64

From a Visual Studio Developer PowerShell:

```powershell
./Build/build-windows.ps1

dotnet run `
  --project Samples/ThinCamDemo.Desktop/ThinCamDemo.Desktop.csproj `
  -c Debug `
  -r win-x64
```

For Windows ARM64:

```powershell
$env:RID = "win-arm64"
./Build/build-windows.ps1

dotnet run `
  --project Samples/ThinCamDemo.Desktop/ThinCamDemo.Desktop.csproj `
  -c Debug `
  -r win-arm64
```

### 10.2 Linux x64

```bash
./Build/build-linux.sh

dotnet run \
  --project Samples/ThinCamDemo.Desktop/ThinCamDemo.Desktop.csproj \
  -c Debug \
  -r linux-x64
```

The process must be allowed to open the selected `/dev/video*` device. Depending on
the distribution, this can require a `video` group membership, an ACL, a udev rule,
or explicit device mapping into a container or sandbox.

### 10.3 macOS

Install the .NET SDK, Xcode command-line tools, CMake, and Ninja, then run:

```bash
./Build/build-macos.sh
./Build/run-demo-macos.sh
```

The script selects `osx-arm64` or `osx-x64` from the host architecture, publishes
the desktop head, creates `Build/Native/artifacts/demo-macos/ThinCam Demo.app`, applies an ad-hoc
signature, and opens the bundle.

### 10.4 Android

```bash
dotnet workload install android

# On Linux/WSL: build-linux.sh builds Linux + Android native libraries.
# On macOS: build-macos.sh builds macOS + iOS + Android native libraries.
# The NDK is auto-discovered from ANDROID_NDK_HOME or the Android SDK.
./Build/build-linux.sh   # or: ./Build/build-macos.sh

dotnet build \
  Samples/ThinCamDemo.Android/ThinCamDemo.Android.csproj \
  -c Debug \
  -p:ThinCamTargetFrameworks=net10.0-android \
  -t:Run
```

A running emulator or connected device is required for `-t:Run`. Use an arm64
device or an x64 emulator matching the native libraries produced by
`build-macos.sh`.

### 10.5 iOS simulator

On macOS:

```bash
dotnet workload install ios
./Build/build-macos.sh

dotnet build \
  Samples/ThinCamDemo.iOS/ThinCamDemo.iOS.csproj \
  -c Debug \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -r iossimulator-arm64
```

To launch with the command line when a simulator is available:

```bash
dotnet build \
  Samples/ThinCamDemo.iOS/ThinCamDemo.iOS.csproj \
  -c Debug \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -r iossimulator-arm64 \
  -t:Run
```

Physical iOS devices require `ios-arm64`, normal Apple signing configuration, and a
provisioning profile.

### 10.6 Build only the reusable control

Desktop target:

```bash
dotnet build Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net8.0 \
  -f net8.0
```

Android target:

```bash
dotnet build Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-android \
  -f net10.0-android
```

iOS target:

```bash
dotnet build Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -f net10.0-ios \
  -r iossimulator-arm64
```

### 10.7 Pack

After all native assets and mobile workloads are available:

```bash
VERSION=0.1.0 ./Build/build-macos.sh
```

This produces `ThinCam`, `ThinCam.SkiaSharp`, and `ThinCam.Avalonia` packages in
`Build/Native/artifacts/packages`.

## 11. Tests

`Tests/ThinCamTests.Avalonia` validates the reusable source independently of a
windowing system:

- Frame data is copied before the caller disposes `VideoFrame`
- Frame version, dimensions, and change notifications are correct
- Rotation changes the published bitmap dimensions and coordinates
- Clearing removes the snapshot and publishes an empty-source event
- Disposed sources reject further operations

`Tests/ThinCamTests.SkiaSharp` continues to cover stride, BGRA ordering, rotation,
mirroring, encoding, and the underlying double-buffer behavior.

CI builds the reusable control and desktop demo on Linux, Windows, and macOS. It
also builds the Android and iOS heads with their platform workloads. UI execution
and physical camera behavior still require manual hardware testing.

## 12. Limitations and extension points

- Preview publication adds a managed-to-Skia pixel copy after ThinCam's required
  native-to-managed copy.
- The control is intentionally not a camera owner. It does not request permission,
  enumerate devices, or start capture.
- The control displays the latest frame and can skip intermediate preview versions
  when the UI cannot keep up.
- One `CameraPreviewSource` can be bound to multiple controls, but each control
  independently draws the same front bitmap under a read lock.
- Transform policy is applied during `Publish`; it is not a live styled property on
  the visual control.
- The placeholder is intentionally simple Skia text. Applications can overlay their
  own Avalonia controls for richer empty/loading/error states.
- No GPU-native camera texture path exists. The current design favors portability,
  clear ownership, and predictable teardown over zero-copy rendering.
- The demo does not implement background-camera continuation. Applications should
  close capture when their platform lifecycle enters a state where camera use is no
  longer appropriate.
