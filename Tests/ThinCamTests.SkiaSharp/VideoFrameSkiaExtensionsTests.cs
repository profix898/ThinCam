using System.Buffers;
using SkiaSharp;
using ThinCam;
using ThinCam.SkiaSharp;

namespace ThinCamTests.SkiaSharp;

/// <summary>Tests conversion and buffering of video frames with SkiaSharp.</summary>
[TestClass]
public sealed class VideoFrameSkiaExtensionsTests
{
    /// <summary>Verifies that copying honors source row padding and BGRA channel order.</summary>
    [TestMethod]
    public void CopyTo_RespectsSourceStrideAndBgraOrder()
    {
        using var frame = CreateFrame(2,
                                      2,
                                      12,
                                      0,
                                      false,
                                      // Two BGRA rows include eight bytes of non-pixel stride padding.
                                      [
                                          1, 2, 3, 255, 4, 5, 6, 255, 91, 92,
                                          93, 94, 7, 8, 9, 255, 10, 11, 12, 255,
                                          95, 96, 97, 98
                                      ]);

        using SKBitmap bitmap = frame.ToSKBitmap();

        AssertColor(bitmap, 0, 0, 3, 2, 1);
        AssertColor(bitmap, 1, 0, 6, 5, 4);
        AssertColor(bitmap, 0, 1, 9, 8, 7);
        AssertColor(bitmap, 1, 1, 12, 11, 10);
    }

    /// <summary>Verifies clockwise 90-degree rotation dimensions and pixel positions.</summary>
    [TestMethod]
    public void Rotation90_ChangesDimensionsAndPixelPositions()
    {
        using var frame = CreateLabelledFrame(2,
                                              3,
                                              90,
                                              false);

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

    /// <summary>Verifies clockwise 270-degree rotation dimensions and pixel positions.</summary>
    [TestMethod]
    public void Rotation270_ChangesDimensionsAndPixelPositions()
    {
        using var frame = CreateLabelledFrame(2,
                                              3,
                                              270,
                                              false);

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

    /// <summary>Verifies that mirroring reverses each unrotated row.</summary>
    [TestMethod]
    public void MirroringWithoutRotation_ReversesEachRow()
    {
        using var frame = CreateLabelledFrame(3,
                                              1,
                                              0,
                                              true);

        using SKBitmap bitmap = frame.ToSKBitmap(SkiaFrameTransform.ApplyMirroring);

        AssertLabel(bitmap, 0, 0, 3);
        AssertLabel(bitmap, 1, 0, 2);
        AssertLabel(bitmap, 2, 0, 1);
    }

    /// <summary>Verifies that presentation mirroring is applied after rotation.</summary>
    [TestMethod]
    public void Presentation_AppliesHorizontalMirrorAfterRotation()
    {
        using var frame = CreateLabelledFrame(2,
                                              3,
                                              90,
                                              true);

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

    /// <summary>Verifies that image conversion uses transformed dimensions.</summary>
    [TestMethod]
    public void ToSKImage_UsesTransformedDimensions()
    {
        using var frame = CreateLabelledFrame(2, 3, 90, false);
        using SKImage image = frame.ToSKImage(SkiaFrameTransform.ApplyRotation);

        Assert.AreEqual(3, image.Width);
        Assert.AreEqual(2, image.Height);
    }

    /// <summary>Verifies that copying rejects a destination with incorrect dimensions.</summary>
    [TestMethod]
    public void CopyTo_RejectsWrongDestinationSize()
    {
        using var frame = CreateLabelledFrame(2, 3, 90, false);
        using var bitmap = new SKBitmap(new SKImageInfo(2, 3, SKColorType.Bgra8888, SKAlphaType.Opaque));

        Assert.ThrowsExactly<ArgumentException>(() =>
                                                    frame.CopyTo(bitmap, SkiaFrameTransform.ApplyRotation));
    }

    /// <summary>Verifies that PNG encoding produces a PNG signature.</summary>
    [TestMethod]
    public void EncodeToBytes_ProducesPng()
    {
        using var frame = CreateLabelledFrame(2, 2, 0, false);

        var encoded = frame.EncodeToBytes(SKEncodedImageFormat.Png);

        Assert.IsTrue(encoded.Length > 8);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
                                  encoded[..8]);
    }

    /// <summary>Verifies that conversion rejects a disposed frame.</summary>
    [TestMethod]
    public void ToSKBitmap_RejectsDisposedFrame()
    {
        var frame = CreateLabelledFrame(1, 1, 0, false);
        frame.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => frame.ToSKBitmap());
    }

    /// <summary>Verifies that encoding rejects quality values outside the valid range.</summary>
    [TestMethod]
    public void EncodeToBytes_RejectsQualityOutsideRange()
    {
        using var frame = CreateLabelledFrame(1, 1, 0, false);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                                                              frame.EncodeToBytes(SKEncodedImageFormat.Jpeg, quality: 101));
    }

    /// <summary>Verifies that the frame buffer publishes its latest frame and snapshots.</summary>
    [TestMethod]
    public void SkiaFrameBuffer_PublishesLatestFrameAndSnapshot()
    {
        using var buffer = new SkiaFrameBuffer();
        using var first = CreateLabelledFrame(1, 1, 0, false);
        using var second = CreateLabelledFrame(1, 1, 0, false, 9);

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

    private static VideoFrame CreateLabelledFrame(int width,
                                                  int height,
                                                  int rotation,
                                                  bool mirrored,
                                                  byte firstLabel = 1)
    {
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var label = firstLabel;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * stride) + (x * 4);
                pixels[offset] = label;
                pixels[offset + 1] = label;
                pixels[offset + 2] = label;
                pixels[offset + 3] = 255;
                label++;
            }
        }

        return CreateFrame(width, height, stride, rotation, mirrored, pixels);
    }

    private static VideoFrame CreateFrame(int width,
                                          int height,
                                          int stride,
                                          int rotation,
                                          bool mirrored,
                                          byte[] pixels)
    {
        var owner = new ArrayMemoryOwner(pixels);
        return new VideoFrame(owner,
                              pixels.Length,
                              width,
                              height,
                              stride,
                              PixelFormat.Bgra32,
                              rotation,
                              mirrored,
                              TimeSpan.Zero);
    }

    private static void AssertLabel(SKBitmap bitmap, int x, int y, byte label) => AssertColor(bitmap, x, y, label, label, label);

    private static void AssertColor(SKBitmap bitmap,
                                    int x,
                                    int y,
                                    byte red,
                                    byte green,
                                    byte blue)
    {
        var color = bitmap.GetPixel(x, y);
        Assert.AreEqual(red, color.Red);
        Assert.AreEqual(green, color.Green);
        Assert.AreEqual(blue, color.Blue);
        Assert.AreEqual(Byte.MaxValue, color.Alpha);
    }

    private sealed class ArrayMemoryOwner(byte[] data) : IMemoryOwner<byte>
    {
        private byte[]? _data = data;

        /// <inheritdoc />
        public Memory<byte> Memory => _data ?? throw new ObjectDisposedException(nameof(ArrayMemoryOwner));

        /// <inheritdoc />
        public void Dispose() => _data = null;
    }
}
