namespace ThinCam.SkiaSharp;

/// <summary>
/// Selects presentation metadata that should be applied while converting a
/// <see cref="VideoFrame" /> to a SkiaSharp bitmap.
/// </summary>
[Flags]
public enum SkiaFrameTransform
{
    /// <summary>Preserve the raw frame orientation and pixel order.</summary>
    None = 0,

    /// <summary>Apply <see cref="VideoFrame.RotationDegrees" /> clockwise.</summary>
    ApplyRotation = 1,

    /// <summary>
    /// Mirror the final presented image horizontally when
    /// <see cref="VideoFrame.IsMirrored" /> is <see langword="true" />.
    /// </summary>
    ApplyMirroring = 2,

    /// <summary>Apply both rotation and mirroring metadata.</summary>
    Presentation = ApplyRotation | ApplyMirroring
}
