namespace ThinCamDemo.Services;

/// <summary>Describes data that the platform should save to a file.</summary>
/// <param name="SuggestedFileName">The suggested file name.</param>
/// <param name="Description">The file type description.</param>
/// <param name="Extension">The file extension without a leading period.</param>
/// <param name="MimeType">The MIME type.</param>
/// <param name="Data">The data to save.</param>
public sealed record SaveFileRequest(string SuggestedFileName,
                                     string Description,
                                     string Extension,
                                     string MimeType,
                                     ReadOnlyMemory<byte> Data);
