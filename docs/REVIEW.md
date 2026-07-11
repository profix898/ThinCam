# Final implementation review

Review date: 2026-07-25

## Scope delivered

- One managed camera API for Windows, Linux, macOS, Android, and iOS.
- Versioned native ABI v2 with 12 exports and capability-driven Exposure, Focus, Zoom, and Light controls.
- Optional `ThinCam.SkiaSharp` package for stride-aware conversion, presentation transforms, encoding, and reusable double buffering.
- Optional `ThinCam.Avalonia` package containing a reusable MVVM-friendly `CameraPreviewSource` and custom-rendered `CameraPreview` control.
- Shared Avalonia demo UI plus thin desktop, Android, and iOS application heads.
- Interactive permission, enumeration, lifecycle, preview, snapshot, controls, frame statistics, diagnostic logging, and report export.
- Native and managed build scripts, package wiring, adapter tests, and cross-platform CI definitions.

## Avalonia design review

- The reusable control does not open, start, stop, or own a camera; lifecycle remains in the application or view model.
- `CameraPreviewSource.Publish` synchronously copies a `VideoFrame` into Skia-owned reusable memory, so the caller can dispose the frame immediately.
- `SkiaFrameBuffer` copies into a hidden back bitmap while renders continue using the front bitmap; only the final swap takes the write lock.
- `CameraPreview` subscribes only while attached to the visual tree and coalesces frame notifications into at most one pending UI invalidation.
- Drawing uses Avalonia's Skia lease through `ICustomDrawOperation`; the control supports stretch modes, background/placeholder styling, snapshots, and once-per-version render notifications.
- The demo depends on `ThinCam.Avalonia` rather than embedding preview code, proving the control is reusable.
- Android camera permission is delegated through a host service that supplies the active `Activity`; `IActivityApplicationLifetime.MainViewFactory` creates a fresh root view for activity recreation, and detached roots dispose their camera state. Apple heads include the required purpose string.
- The macOS helper creates and signs an application bundle so the camera purpose string is available during source testing.

## Validation completed in this environment

- Linux backend rebuilt in Release mode with warnings as errors.
- Native YUYV/UYVY/YUV420 conversion tests passed.
- ABI version 2 and all 12 expected exports were verified.
- Native C ABI header compiled in standalone C11 and C++17 checks.
- Every `.csproj`, `.props`, `.targets`, Avalonia XAML, Android manifest, and Apple plist parsed as XML.
- GitHub Actions YAML parsed successfully.
- Bash scripts passed `bash -n` validation; the PowerShell script was inspected structurally.
- Markdown local links, heading anchors, and code fences were checked.
- Public terminology uses `Light`; native platform identifiers may retain platform SDK terminology internally.
- Repository archives were generated from a clean source selection and checksummed.

## Validation not possible here

The container does not contain the .NET SDK, Visual Studio/Windows SDK, Xcode, Android NDK, Avalonia runtime, or camera hardware. Therefore:

- C# compilation, managed tests, NuGet packing, and application launch were not executed locally.
- Windows, Apple, and Android native compilation was not executed locally.
- No Avalonia visual render, storage picker, permission dialog, live preview, control adjustment, suspend/resume, disconnect, or multi-device hardware test was possible.

The included CI workflow performs the missing managed and platform compilation when run on appropriate hosted runners. Physical-device testing is still required before a production release.

## Known current constraints

- Capture output is BGRA32 and incurs one native-to-managed copy per delivered frame.
- The Skia/Avalonia preview path performs a second copy into reusable Skia-owned memory.
- Linux rejects cameras that expose only compressed formats such as MJPEG.
- One active managed frame consumer is allowed per camera; application fan-out remains the caller's responsibility.
- Control availability, ranges, quantization, and semantics depend on the device and driver.
- Android rotation is sensor-relative; Apple capture currently reports zero rotation.
- The demo has desktop, Android, and iOS heads but no browser head because ThinCam has no browser camera backend.
- There is no audio, video recording, hot-plug notification, direct GPU camera surface, white-balance control, or still-photo flash sequencing yet.
