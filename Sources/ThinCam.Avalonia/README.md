# ThinCam.Avalonia

Reusable Avalonia preview components for ThinCam.

The package contains two deliberately separate types:

- `CameraPreviewSource` is a thread-safe, MVVM-friendly presentation source. A camera loop publishes `VideoFrame` instances into it from any thread.
- `CameraPreview` is an Avalonia `Control` that renders the latest source frame directly through Avalonia's Skia lease and coalesces UI invalidations.

```xml
<tc:CameraPreview Source="{Binding PreviewSource}"
                  Stretch="Uniform"
                  PlaceholderText="Open a camera" />
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

The control does not open, start, stop, or own a camera. This keeps capture lifecycle and permission handling in the application while allowing the same preview control to be reused in MVVM, code-behind, or custom composition architectures.
