# ThinCam.SkiaSharp

Optional SkiaSharp adapters for [ThinCam](https://www.nuget.org/packages/ThinCam).

The package converts ThinCam's pooled BGRA32 camera frames into Skia-owned bitmaps and images, applies optional presentation rotation and mirroring, encodes snapshots, and provides a reusable double-buffer for live preview rendering. It does not change the dependency-free ThinCam capture core.

On Linux, the host application must also reference either `SkiaSharp.NativeAssets.Linux` or `SkiaSharp.NativeAssets.Linux.NoDependencies`. Windows, macOS, Android, and iOS native assets are selected by the main SkiaSharp package for their platform targets.

```csharp
using SkiaSharp;
using ThinCam;
using ThinCam.SkiaSharp;

await foreach (VideoFrame frame in camera.GetFramesAsync(token))
{
    using (frame)
    using (SKBitmap bitmap = frame.ToSKBitmap(
        SkiaFrameTransform.Presentation))
    {
        // Draw, inspect, or encode the bitmap.
    }
}

using SKImage image = frame.ToSKImage(
    SkiaFrameTransform.Presentation);
```

For a live preview, reuse memory:

```csharp
using var buffer = new SkiaFrameBuffer();

await foreach (VideoFrame frame in camera.GetFramesAsync(token))
{
    using (frame)
    {
        buffer.Update(frame, SkiaFrameTransform.Presentation);
    }
}

// On the render thread:
buffer.TryDraw(canvas, destination);
```

Encode a snapshot:

```csharp
byte[] jpeg = frame.EncodeToBytes(
    SKEncodedImageFormat.Jpeg,
    quality: 90,
    SkiaFrameTransform.Presentation);
```

`VideoFrame` remains owned by the caller and must be disposed. Every conversion copies into Skia-owned memory; the package deliberately avoids pinning pooled camera buffers or retaining them beyond their callback lifetime.
