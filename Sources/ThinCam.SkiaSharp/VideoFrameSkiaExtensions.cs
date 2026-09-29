using System.Diagnostics;
using SkiaSharp;

namespace ThinCam.SkiaSharp;

/// <summary>SkiaSharp conversion and encoding helpers for ThinCam frames.</summary>
public static class VideoFrameSkiaExtensions
{
    private const int BytesPerPixel = 4;

    /// <summary>
    /// Returns the bitmap dimensions produced by the requested transform.
    /// </summary>
    public static SKSizeI GetSkiaSize(this VideoFrame frame,
                                      SkiaFrameTransform transform = SkiaFrameTransform.None)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateTransform(transform);
        ValidateFrame(frame);

        var rotation = GetAppliedRotation(frame, transform);
        return rotation is 90 or 270
            ? new SKSizeI(frame.Height, frame.Width)
            : new SKSizeI(frame.Width, frame.Height);
    }

    /// <summary>
    /// Allocates a Skia-owned BGRA8888 bitmap and copies the frame into it.
    /// Use <see cref="CopyTo(VideoFrame, SKBitmap, SkiaFrameTransform)" /> or
    /// <see cref="SkiaFrameBuffer" /> for live preview loops that should reuse memory.
    /// </summary>
    public static SKBitmap ToSKBitmap(this VideoFrame frame,
                                      SkiaFrameTransform transform = SkiaFrameTransform.None)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var size = frame.GetSkiaSize(transform);
        var bitmap = new SKBitmap(CreateImageInfo(size.Width, size.Height));

        try
        {
            frame.CopyTo(bitmap, transform);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates an immutable Skia image containing a copy of the frame pixels.
    /// The caller owns the returned image and must dispose it.
    /// </summary>
    public static SKImage ToSKImage(this VideoFrame frame,
                                    SkiaFrameTransform transform = SkiaFrameTransform.None)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var bitmap = frame.ToSKBitmap(transform);
        return SKImage.FromBitmap(bitmap)
               ?? throw new InvalidOperationException("SkiaSharp could not create an image from the converted bitmap.");
    }

    /// <summary>
    /// Copies the frame into an existing Skia-owned BGRA8888 bitmap.
    /// The destination dimensions must match <see cref="GetSkiaSize" />.
    /// </summary>
    public static unsafe void CopyTo(this VideoFrame frame,
                                     SKBitmap destination,
                                     SkiaFrameTransform transform = SkiaFrameTransform.None)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateTransform(transform);
        ValidateFrame(frame);

        var requiredSize = frame.GetSkiaSize(transform);
        ValidateDestination(destination, requiredSize);

        var destinationPixels = destination.GetPixels();
        if (destinationPixels == 0)
            throw new InvalidOperationException("The destination SKBitmap does not expose writable pixel memory.");

        var destinationStride = destination.RowBytes;
        var requiredDestinationRowLength = checked(requiredSize.Width * BytesPerPixel);
        if (destinationStride < requiredDestinationRowLength)
            throw new InvalidOperationException("The destination SKBitmap row stride is smaller than its visible BGRA row.");

        var source = frame.Data.Span;
        var rotation = GetAppliedRotation(frame, transform);
        var mirror = ShouldMirror(frame, transform);

        if (rotation == 0 && !mirror)
        {
            CopyRows(source,
                     frame.Width,
                     frame.Height,
                     frame.Stride,
                     (byte*) destinationPixels,
                     destinationStride);
        }
        else
        {
            CopyTransformed(source,
                            frame.Width,
                            frame.Height,
                            frame.Stride,
                            (byte*) destinationPixels,
                            requiredSize.Width,
                            destinationStride,
                            rotation,
                            mirror);
        }

        destination.NotifyPixelsChanged();
    }

    /// <summary>
    /// Encodes one frame using SkiaSharp. The returned <see cref="SKData" /> is
    /// owned by the caller and must be disposed.
    /// </summary>
    public static SKData Encode(this VideoFrame frame,
                                SKEncodedImageFormat format,
                                int quality = 100,
                                SkiaFrameTransform transform = SkiaFrameTransform.None)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateQuality(quality);

        using var image = frame.ToSKImage(transform);
        return image.Encode(format, quality)
               ?? throw new InvalidOperationException($"SkiaSharp could not encode the frame as {format}.");
    }

    /// <summary>
    /// Encodes one frame and copies the result into a managed byte array.
    /// Prefer <see cref="Encode" /> when the caller can consume <see cref="SKData" />
    /// directly without another copy.
    /// </summary>
    public static byte[] EncodeToBytes(this VideoFrame frame,
                                       SKEncodedImageFormat format,
                                       int quality = 100,
                                       SkiaFrameTransform transform = SkiaFrameTransform.None)
    {
        using var data = frame.Encode(format, quality, transform);
        return data.ToArray();
    }

    internal static SKImageInfo CreateImageInfo(int width, int height) => new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);

    private static void ValidateFrame(VideoFrame frame)
    {
        if (frame.PixelFormat != PixelFormat.Bgra32)
            throw new NotSupportedException($"ThinCam.SkiaSharp supports BGRA32 frames, not {frame.PixelFormat}.");

        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentException("The frame dimensions must be positive.", nameof(frame));

        var visibleRowLength = checked(frame.Width * BytesPerPixel);
        if (frame.Stride < visibleRowLength)
        {
            throw new ArgumentException("The frame stride is smaller than its visible BGRA row.",
                                        nameof(frame));
        }

        var requiredLength = checked((long) frame.Stride * frame.Height);
        if (frame.DataLength < requiredLength)
        {
            throw new ArgumentException("The frame data is smaller than height multiplied by stride.",
                                        nameof(frame));
        }

        // Accessing Data here also gives callers an immediate, predictable
        // ObjectDisposedException when the VideoFrame has already been released.
        _ = frame.Data;
    }

    private static void ValidateDestination(SKBitmap destination, SKSizeI size)
    {
        if (destination.Width != size.Width || destination.Height != size.Height)
        {
            throw new ArgumentException($"The destination bitmap must be {size.Width}x{size.Height}.",
                                        nameof(destination));
        }

        if (destination.ColorType != SKColorType.Bgra8888)
        {
            throw new ArgumentException("The destination bitmap must use SKColorType.Bgra8888.",
                                        nameof(destination));
        }
    }

    private static void ValidateTransform(SkiaFrameTransform transform)
    {
        const SkiaFrameTransform known = SkiaFrameTransform.Presentation;
        if ((transform & ~known) != 0)
            throw new ArgumentOutOfRangeException(nameof(transform));
    }

    private static void ValidateQuality(int quality)
    {
        if (quality is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(quality),
                                                  quality,
                                                  "Encoding quality must be between 0 and 100.");
        }
    }

    private static int GetAppliedRotation(VideoFrame frame, SkiaFrameTransform transform)
    {
        if ((transform & SkiaFrameTransform.ApplyRotation) == 0)
            return 0;

        var normalized = ((frame.RotationDegrees % 360) + 360) % 360;
        if (normalized is not (0 or 90 or 180 or 270))
            throw new NotSupportedException($"ThinCam.SkiaSharp supports right-angle rotation metadata; received {frame.RotationDegrees} degrees.");

        return normalized;
    }

    private static bool ShouldMirror(VideoFrame frame, SkiaFrameTransform transform) => frame.IsMirrored && (transform & SkiaFrameTransform.ApplyMirroring) != 0;

    private static unsafe void CopyRows(ReadOnlySpan<byte> source,
                                        int width,
                                        int height,
                                        int sourceStride,
                                        byte* destination,
                                        int destinationStride)
    {
        var rowLength = checked(width * BytesPerPixel);

        for (var y = 0; y < height; y++)
        {
            var sourceRow = source.Slice(checked(y * sourceStride),
                                         rowLength);
            Span<byte> destinationRow = new Span<byte>(destination + checked(y * destinationStride), rowLength);
            sourceRow.CopyTo(destinationRow);
        }
    }

    private static unsafe void CopyTransformed(ReadOnlySpan<byte> source,
                                               int sourceWidth,
                                               int sourceHeight,
                                               int sourceStride,
                                               byte* destination,
                                               int destinationWidth,
                                               int destinationStride,
                                               int rotation,
                                               bool mirror)
    {
        for (var sourceY = 0; sourceY < sourceHeight; sourceY++)
        {
            var sourceRowOffset = checked(sourceY * sourceStride);

            for (var sourceX = 0; sourceX < sourceWidth; sourceX++)
            {
                int rotatedX;
                int rotatedY;

                // Map each source pixel through clockwise rotation before horizontal mirroring.
                switch (rotation)
                {
                    case 0:
                        rotatedX = sourceX;
                        rotatedY = sourceY;
                        break;
                    case 90:
                        rotatedX = sourceHeight - 1 - sourceY;
                        rotatedY = sourceX;
                        break;
                    case 180:
                        rotatedX = sourceWidth - 1 - sourceX;
                        rotatedY = sourceHeight - 1 - sourceY;
                        break;
                    case 270:
                        rotatedX = sourceY;
                        rotatedY = sourceWidth - 1 - sourceX;
                        break;
                    default:
                        throw new UnreachableException();
                }

                var destinationX = mirror
                    ? destinationWidth - 1 - rotatedX
                    : rotatedX;

                var sourceOffset = checked(sourceRowOffset + (sourceX * BytesPerPixel));
                var destinationOffset = checked(
                    (rotatedY * destinationStride) + (destinationX * BytesPerPixel));

                destination[destinationOffset] = source[sourceOffset];
                destination[destinationOffset + 1] = source[sourceOffset + 1];
                destination[destinationOffset + 2] = source[sourceOffset + 2];
                destination[destinationOffset + 3] = source[sourceOffset + 3];
            }
        }
    }
}
