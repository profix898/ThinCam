using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using ThinCam;
using ThinCam.SkiaSharp;

namespace ThinCam.SkiaSharp.Tests;

[TestClass]
public sealed class VideoFrameSkiaExtensionsTests
{
    [TestMethod]
    public void CopyTo_RespectsSourceStrideAndBgraOrder()
    {
        using VideoFrame frame = CreateFrame(
            width: 2,
            height: 2,
            stride: 12,
            rotation: 0,
            mirrored: false,
            pixels:
            [
                1, 2, 3, 255, 4, 5, 6, 255, 91, 92, 93, 94,
                7, 8, 9, 255, 10, 11, 12, 255, 95, 96, 97, 98
            ]);

        using SKBitmap bitmap = frame.ToSKBitmap();

        AssertColor(bitmap, 0, 0, red: 3, green: 2, blue: 1);
        AssertColor(bitmap, 1, 0, red: 6, green: 5, blue: 4);
        AssertColor(bitmap, 0, 1, red: 9, green: 8, blue: 7);
        AssertColor(bitmap, 1, 1, red: 12, green: 11, blue: 10);
    }

    [TestMethod]
    public void Rotation90_ChangesDimensionsAndPixelPositions()
    {
        using VideoFrame frame = CreateLabelledFrame(
            width: 2,
            height: 3,
            rotation: 90,
            mirrored: false);

        using SKBitmap bitmap = frame.ToSKBitmap(SkiaFrameTransform.ApplyRotation);

        Assert.AreEqual(3, bitmap.Width);
        Assert.AreEqual(2, bitmap.Height);

        // Source labels:
        // 1 2
        // 3 4
        // 5 6
        // Clockwise 90 degrees:
        // 5 3 1
        // 6 4 2
        AssertLabel(bitmap, 0, 0, 5);
        AssertLabel(bitmap, 1, 0, 3);
        AssertLabel(bitmap, 2, 0, 1);
        AssertLabel(bitmap, 0, 1, 6);
        AssertLabel(bitmap, 1, 1, 4);
        AssertLabel(bitmap, 2, 1, 2);
    }


    [TestMethod]
    public void Rotation270_ChangesDimensionsAndPixelPositions()
    {
        using VideoFrame frame = CreateLabelledFrame(
            width: 2,
            height: 3,
            rotation: 270,
            mirrored: false);

        using SKBitmap bitmap = frame.ToSKBitmap(SkiaFrameTransform.ApplyRotation);

        Assert.AreEqual(3, bitmap.Width);
        Assert.AreEqual(2, bitmap.Height);

        // Source labels:
        // 1 2
        // 3 4
        // 5 6
        // Clockwise 270 degrees:
        // 2 4 6
        // 1 3 5
        AssertLabel(bitmap, 0, 0, 2);
        AssertLabel(bitmap, 1, 0, 4);
        AssertLabel(bitmap, 2, 0, 6);
        AssertLabel(bitmap, 0, 1, 1);
        AssertLabel(bitmap, 1, 1, 3);
        AssertLabel(bitmap, 2, 1, 5);
    }

    [TestMethod]
    public void MirroringWithoutRotation_ReversesEachRow()
    {
        using VideoFrame frame = CreateLabelledFrame(
            width: 3,
            height: 1,
            rotation: 0,
            mirrored: true);

        using SKBitmap bitmap = frame.ToSKBitmap(SkiaFrameTransform.ApplyMirroring);

        AssertLabel(bitmap, 0, 0, 3);
        AssertLabel(bitmap, 1, 0, 2);
        AssertLabel(bitmap, 2, 0, 1);
    }

    [TestMethod]
    public void Presentation_AppliesHorizontalMirrorAfterRotation()
    {
        using VideoFrame frame = CreateLabelledFrame(
            width: 2,
            height: 3,
            rotation: 90,
            mirrored: true);

        using SKBitmap bitmap = frame.ToSKBitmap(SkiaFrameTransform.Presentation);

        // Rotated result mirrored horizontally:
        // 1 3 5
        // 2 4 6
        AssertLabel(bitmap, 0, 0, 1);
        AssertLabel(bitmap, 1, 0, 3);
        AssertLabel(bitmap, 2, 0, 5);
        AssertLabel(bitmap, 0, 1, 2);
        AssertLabel(bitmap, 1, 1, 4);
        AssertLabel(bitmap, 2, 1, 6);
    }


    [TestMethod]
    public void ToSKImage_UsesTransformedDimensions()
    {
        using VideoFrame frame = CreateLabelledFrame(2, 3, 90, false);
        using SKImage image = frame.ToSKImage(SkiaFrameTransform.ApplyRotation);

        Assert.AreEqual(3, image.Width);
        Assert.AreEqual(2, image.Height);
    }

    [TestMethod]
    public void CopyTo_RejectsWrongDestinationSize()
    {
        using VideoFrame frame = CreateLabelledFrame(2, 3, 90, false);
        using var bitmap = new SKBitmap(
            new SKImageInfo(2, 3, SKColorType.Bgra8888, SKAlphaType.Opaque));

        Assert.ThrowsExactly<ArgumentException>(() =>
            frame.CopyTo(bitmap, SkiaFrameTransform.ApplyRotation));
    }

    [TestMethod]
    public void EncodeToBytes_ProducesPng()
    {
        using VideoFrame frame = CreateLabelledFrame(2, 2, 0, false);

        byte[] encoded = frame.EncodeToBytes(SKEncodedImageFormat.Png);

        Assert.IsTrue(encoded.Length > 8);
        CollectionAssert.AreEqual(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            encoded[..8]);
    }


    [TestMethod]
    public void ToSKBitmap_RejectsDisposedFrame()
    {
        VideoFrame frame = CreateLabelledFrame(1, 1, 0, false);
        frame.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => frame.ToSKBitmap());
    }

    [TestMethod]
    public void EncodeToBytes_RejectsQualityOutsideRange()
    {
        using VideoFrame frame = CreateLabelledFrame(1, 1, 0, false);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            frame.EncodeToBytes(SKEncodedImageFormat.Jpeg, quality: 101));
    }

    [TestMethod]
    public void SkiaFrameBuffer_PublishesLatestFrameAndSnapshot()
    {
        using var buffer = new SkiaFrameBuffer();
        using VideoFrame first = CreateLabelledFrame(1, 1, 0, false, firstLabel: 1);
        using VideoFrame second = CreateLabelledFrame(1, 1, 0, false, firstLabel: 9);

        Assert.IsFalse(buffer.TryUse(_ => { }));
        buffer.Update(first);
        buffer.Update(second);

        Assert.AreEqual(2L, buffer.Version);
        long leasedVersion = 0;
        Assert.IsTrue(buffer.TryUse((_, version) => leasedVersion = version));
        Assert.AreEqual(buffer.Version, leasedVersion);
        using SKBitmap? snapshot = buffer.CopySnapshot();
        Assert.IsNotNull(snapshot);
        AssertLabel(snapshot!, 0, 0, 9);
    }

    private static VideoFrame CreateLabelledFrame(
        int width,
        int height,
        int rotation,
        bool mirrored,
        byte firstLabel = 1)
    {
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        byte label = firstLabel;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * 4;
                pixels[offset] = label;
                pixels[offset + 1] = label;
                pixels[offset + 2] = label;
                pixels[offset + 3] = 255;
                label++;
            }
        }

        return CreateFrame(width, height, stride, rotation, mirrored, pixels);
    }

    private static VideoFrame CreateFrame(
        int width,
        int height,
        int stride,
        int rotation,
        bool mirrored,
        byte[] pixels)
    {
        var owner = new ArrayMemoryOwner(pixels);
        return new VideoFrame(
            owner,
            pixels.Length,
            width,
            height,
            stride,
            PixelFormat.Bgra32,
            rotation,
            mirrored,
            TimeSpan.Zero);
    }

    private static void AssertLabel(SKBitmap bitmap, int x, int y, byte label) =>
        AssertColor(bitmap, x, y, label, label, label);

    private static void AssertColor(
        SKBitmap bitmap,
        int x,
        int y,
        byte red,
        byte green,
        byte blue)
    {
        SKColor color = bitmap.GetPixel(x, y);
        Assert.AreEqual(red, color.Red);
        Assert.AreEqual(green, color.Green);
        Assert.AreEqual(blue, color.Blue);
        Assert.AreEqual(byte.MaxValue, color.Alpha);
    }

    private sealed class ArrayMemoryOwner(byte[] data) : IMemoryOwner<byte>
    {
        private byte[]? _data = data;

        public Memory<byte> Memory =>
            _data ?? throw new ObjectDisposedException(nameof(ArrayMemoryOwner));

        public void Dispose() => _data = null;
    }
}
