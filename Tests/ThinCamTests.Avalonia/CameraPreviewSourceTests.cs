using System.Buffers;
using ThinCam;
using ThinCam.Avalonia;
using ThinCam.SkiaSharp;

namespace ThinCamTests.Avalonia;

/// <summary>Tests publication and lifetime behavior of camera preview frames.</summary>
[TestClass]
public sealed class CameraPreviewSourceTests
{
    /// <summary>Verifies that publishing copies a frame and raises a change event.</summary>
    [TestMethod]
    public void Publish_CopiesFrameAndRaisesChangedEvent()
    {
        using var source = new CameraPreviewSource();
        using var frame = CreateFrame(37);
        CameraPreviewFrameEventArgs? observed = null;
        source.FrameChanged += (_, args) => observed = args;

        source.Publish(frame, SkiaFrameTransform.None);
        frame.Dispose();

        Assert.IsTrue(source.HasFrame);
        Assert.IsNotNull(observed);
        Assert.IsTrue(observed!.HasFrame);
        Assert.AreEqual(1, observed.PixelWidth);
        Assert.AreEqual(1, observed.PixelHeight);
        Assert.AreEqual(source.Version, observed.Version);

        using var snapshot = source.CopySnapshot();
        Assert.IsNotNull(snapshot);
        var color = snapshot!.GetPixel(0, 0);
        Assert.AreEqual((byte) 37, color.Red);
        Assert.AreEqual((byte) 37, color.Green);
        Assert.AreEqual((byte) 37, color.Blue);
        Assert.AreEqual(Byte.MaxValue, color.Alpha);
    }

    /// <summary>Verifies that clearing removes the frame and raises an empty event.</summary>
    [TestMethod]
    public void Clear_RemovesFrameAndRaisesEmptyEvent()
    {
        using var source = new CameraPreviewSource();
        using var frame = CreateFrame(1);
        source.Publish(frame);
        CameraPreviewFrameEventArgs? observed = null;
        source.FrameChanged += (_, args) => observed = args;

        source.Clear();

        Assert.IsFalse(source.HasFrame);
        Assert.IsNull(source.CopySnapshot());
        Assert.IsNotNull(observed);
        Assert.IsFalse(observed!.HasFrame);
        Assert.AreEqual(0, observed.PixelWidth);
        Assert.AreEqual(0, observed.PixelHeight);
    }

    /// <summary>Verifies that publication applies the frame presentation transform.</summary>
    [TestMethod]
    public void Publish_AppliesPresentationTransform()
    {
        using var source = new CameraPreviewSource();
        using var frame = CreateFrame(width: 2,
                                      height: 1,
                                      rotation: 90,
                                      mirrored: false,
                                      pixels: [1, 1, 1, 255, 2, 2, 2, 255]);

        source.Publish(frame);

        using var snapshot = source.CopySnapshot();
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(1, snapshot!.Width);
        Assert.AreEqual(2, snapshot.Height);
        Assert.AreEqual((byte) 1, snapshot.GetPixel(0, 0).Red);
        Assert.AreEqual((byte) 2, snapshot.GetPixel(0, 1).Red);
    }

    /// <summary>Verifies that a disposed preview source rejects further operations.</summary>
    [TestMethod]
    public void DisposedSource_RejectsOperations()
    {
        var source = new CameraPreviewSource();
        source.Dispose();
        using var frame = CreateFrame(5);

        Assert.ThrowsExactly<ObjectDisposedException>(() => source.Publish(frame));
        Assert.ThrowsExactly<ObjectDisposedException>(() => source.Clear());
        Assert.ThrowsExactly<ObjectDisposedException>(() => source.CopySnapshot());
    }

    private static VideoFrame CreateFrame(byte label = 0,
                                          int width = 1,
                                          int height = 1,
                                          int rotation = 0,
                                          bool mirrored = false,
                                          byte[]? pixels = null)
    {
        var stride = checked(width * 4);
        pixels ??= [label, label, label, 255];
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

    private sealed class ArrayMemoryOwner(byte[] data) : IMemoryOwner<byte>
    {
        private byte[]? _data = data;

        /// <inheritdoc />
        public Memory<byte> Memory => _data ?? throw new ObjectDisposedException(nameof(ArrayMemoryOwner));

        /// <inheritdoc />
        public void Dispose() => _data = null;
    }
}
