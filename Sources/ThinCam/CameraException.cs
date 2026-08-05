namespace ThinCam;

/// <summary>
/// Represents an error reported by a camera operation.
/// </summary>
public sealed class CameraException : Exception
{
    /// <summary>
    /// Initializes a new camera exception.
    /// </summary>
    /// <param name="errorCode">The camera error code.</param>
    /// <param name="message">The error message.</param>
    public CameraException(CameraErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new camera exception with an underlying exception.
    /// </summary>
    /// <param name="errorCode">The camera error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this error.</param>
    public CameraException(CameraErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the camera error code.
    /// </summary>
    public CameraErrorCode ErrorCode { get; }
}
