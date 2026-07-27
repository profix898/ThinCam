# ThinCam.SkiaSharp guide

`ThinCam.SkiaSharp` is an optional adapter package that converts ThinCam's raw BGRA32 camera frames into SkiaSharp objects. It is intentionally separate from the capture core so headless and non-Skia consumers do not inherit a graphics dependency.

## Package responsibilities

The adapter provides:

- Stride-aware copying into `SKBitmap`.
- A convenience `ToSKBitmap` allocation API for snapshots and occasional conversion.
- Optional application of ThinCam rotation and mirroring metadata.
- PNG, JPEG, WebP, and other Skia-supported encoding through `SKImage.Encode`.
- A reusable thread-safe double buffer for live preview rendering.

The adapter does not:

- Change ThinCam's native capture or memory ownership model.
- Retain a native camera buffer.
- Pin ThinCam's pooled managed frame memory.
- Add SkiaSharp to the core `ThinCam` package.
- Provide an Avalonia control or depend on any UI toolkit.

## Installation

```bash
dotnet add package ThinCam
dotnet add package ThinCam.SkiaSharp
```

The adapter currently references SkiaSharp `4.150.1`. Desktop applications must ensure that the appropriate SkiaSharp native assets are available. Avalonia applications normally receive a Skia runtime through Avalonia's Skia backend; a headless Linux application may need `SkiaSharp.NativeAssets.Linux` or `SkiaSharp.NativeAssets.Linux.NoDependencies`.

## Raw conversion

```csharp
using SkiaSharp;
using ThinCam;
using ThinCam.SkiaSharp;

await foreach (VideoFrame frame in camera.GetFramesAsync(token))
{
    using (frame)
    using (SKBitmap bitmap = frame.ToSKBitmap())
    {
        // The bitmap preserves the raw frame orientation.
    }
}
```

`ToSKBitmap` allocates a Skia-owned bitmap and copies the visible camera pixels into it. Source row padding is not copied into visible pixels. Use `ToSKImage` when an immutable, caller-owned image is a better fit:

```csharp
using SKImage image = frame.ToSKImage(
    SkiaFrameTransform.Presentation);
```

The bitmap and image use:

```text
SKColorType.Bgra8888
SKAlphaType.Opaque
```

ThinCam currently normalizes frame alpha to 255.

## Presentation transforms

ThinCam stores rotation and mirroring as frame metadata. The raw pixels are not transformed by the capture library. Apply that metadata explicitly when producing a display image:

```csharp
using SKBitmap bitmap = frame.ToSKBitmap(
    SkiaFrameTransform.Presentation);
```

Available flags:

```csharp
SkiaFrameTransform.None
SkiaFrameTransform.ApplyRotation
SkiaFrameTransform.ApplyMirroring
SkiaFrameTransform.Presentation
```

Rotation is applied clockwise first. When mirroring is requested and `frame.IsMirrored` is true, the final presented image is mirrored horizontally. Right-angle rotations of 0, 90, 180, and 270 degrees are supported. A 90- or 270-degree rotation swaps the output width and height.

Query the required destination size before reusing a bitmap:

```csharp
SKSizeI size = frame.GetSkiaSize(
    SkiaFrameTransform.Presentation);
```

## Reusing an existing bitmap

For repeated conversion, allocate once and copy into the same bitmap:

```csharp
SKSizeI size = frame.GetSkiaSize(transform);
using var bitmap = new SKBitmap(
    new SKImageInfo(
        size.Width,
        size.Height,
        SKColorType.Bgra8888,
        SKAlphaType.Opaque));

frame.CopyTo(bitmap, transform);
```

The destination must:

- Match the transformed dimensions.
- Use `SKColorType.Bgra8888`.
- Expose writable pixel memory.
- Have a row stride large enough for `width * 4` visible bytes.

The helper calls `SKBitmap.NotifyPixelsChanged` after the copy.

## Reusable live-preview buffer

Allocating a new bitmap for every frame is unsuitable for a sustained preview. `SkiaFrameBuffer` owns two reusable bitmaps. One is published to readers while the next frame is copied into the other.

Capture side:

```csharp
using var previewBuffer = new SkiaFrameBuffer();

await foreach (VideoFrame frame in camera.GetFramesAsync(token))
{
    using (frame)
    {
        previewBuffer.Update(
            frame,
            SkiaFrameTransform.Presentation);
    }

    RequestUiRedraw();
}
```

Render side:

```csharp
previewBuffer.TryDraw(
    canvas,
    new SKRect(0, 0, viewportWidth, viewportHeight));
```

Or access the current bitmap through a protected callback:

```csharp
previewBuffer.TryUse(bitmap =>
{
    canvas.DrawBitmap(bitmap, destination);
});
```

The callback must not retain the bitmap. Its lifetime belongs to the buffer. `Version` increases after every published update and can be used to suppress redundant redraws.

`CopySnapshot` creates an independent bitmap when a caller needs ownership beyond the protected callback:

```csharp
using SKBitmap? snapshot = previewBuffer.CopySnapshot();
```

`Clear` releases both reusable bitmaps and advances the buffer version so a bound presentation layer can return to its placeholder state:

```csharp
previewBuffer.Clear();
```

For a reusable Avalonia control built on this buffer, see [AVALONIA.md](AVALONIA.md).

## Encoding

Return disposable Skia data when possible:

```csharp
using SKData jpeg = frame.Encode(
    SKEncodedImageFormat.Jpeg,
    quality: 90,
    SkiaFrameTransform.Presentation);
```

Create managed bytes when an API specifically requires a byte array:

```csharp
byte[] png = frame.EncodeToBytes(
    SKEncodedImageFormat.Png,
    quality: 100,
    SkiaFrameTransform.Presentation);
```

Encoding quality must be between 0 and 100. The exact interpretation depends on the selected encoder. PNG is lossless, so the quality value does not represent JPEG-style visual quality.

## Memory and performance model

A normal preview frame follows this path:

```text
OS camera buffer
    -> ThinCam pooled managed VideoFrame copy
    -> Skia-owned reusable bitmap copy
    -> UI/GPU presentation
```

The second copy is deliberate. ThinCam frames own pooled managed memory and are disposed by the consumer. Installing that memory directly into an `SKBitmap` would require pinning it and coupling `SKBitmap` disposal to `VideoFrame` disposal. That creates difficult lifetime, pool-retention, and dropped-frame behavior.

For 1920x1080 BGRA32, one visible frame is about 7.9 MiB. A double buffer therefore retains roughly 15.8 MiB plus row padding and object overhead. Applications should choose capture dimensions appropriate for their preview and processing needs.

## Threading rules

`VideoFrame` conversion is synchronous and must complete before the frame is disposed.

`SkiaFrameBuffer` synchronizes writers and readers:

- An update gate ensures that only one frame copy is in progress.
- `Update` copies into the hidden back bitmap while readers continue using the published front bitmap.
- A short write lease is taken only to swap the front and back references.
- `TryUse` and `TryDraw` hold a read lease while the current bitmap is consumed. The two-argument `TryUse` overload supplies the publication version belonging to that exact bitmap.
- A reader callback must not call back into `Update` or `Dispose`, and must not retain the bitmap.
- The buffer can be disposed repeatedly, but it must not be used after disposal.

An Avalonia application should run the ThinCam frame loop away from the UI thread, update the `SkiaFrameBuffer`, and post a coalesced visual invalidation to the UI dispatcher.

## Error behavior

The adapter throws:

- `ObjectDisposedException` for an already-disposed `VideoFrame` or `SkiaFrameBuffer`.
- `NotSupportedException` for unsupported pixel formats or non-right-angle presentation rotation.
- `ArgumentException` for invalid dimensions, stride, frame size, or destination bitmap shape and color type.
- `ArgumentOutOfRangeException` for unknown transform flags or encoding quality outside 0 through 100.
- `InvalidOperationException` when Skia cannot expose writable pixels, copy a snapshot, or encode an image.

## Testing

`Tests/ThinCamTests.SkiaSharp` covers:

- Source stride and row padding.
- BGRA channel order and opaque alpha.
- 90-degree dimension and coordinate mapping.
- Mirroring after rotation.
- Destination validation.
- Transformed `SKImage` dimensions.
- PNG encoding.
- Reusable buffer publication and snapshots.

Run on Linux with:

```bash
dotnet test Tests/ThinCamTests.SkiaSharp/ThinCamTests.SkiaSharp.csproj \
  -c Release
```

The test project includes `SkiaSharp.NativeAssets.Linux.NoDependencies` for its Linux runtime. Production applications should select the native asset package appropriate for their deployment environment.
