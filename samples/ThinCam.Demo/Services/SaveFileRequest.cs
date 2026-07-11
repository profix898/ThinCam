namespace ThinCam.Demo.Services;

public sealed record SaveFileRequest(
    string SuggestedFileName,
    string Description,
    string Extension,
    string MimeType,
    ReadOnlyMemory<byte> Data);
