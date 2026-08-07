namespace ThinCam;

/// <summary>
/// Represents an error reported by a camera operation.
/// </summary>
public sealed class CameraException : Exception
{
    /// <summary>
    /// Initializes a camera exception with an error code and message.
    /// </summary>
    /// <param name="errorCode">The camera error code.</param>
    /// <param name="message">The error message.</param>
    public CameraException(CameraErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a camera exception with an error code, message, and inner exception.
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
    /// Initializes a camera exception with an error code, message, control name, and inner exception.
    /// </summary>
    /// <param name="errorCode">The camera error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="controlName">The name of the camera control involved.</param>
    /// <param name="innerException">The exception that caused this error.</param>
    public CameraException(CameraErrorCode errorCode, string message, string? controlName, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        ControlName = controlName;
    }

    /// <summary>
    /// Gets the name of the camera control involved, when applicable.
    /// </summary>
    public string? ControlName { get; }

    /// <summary>
    /// Gets the camera error code.
    /// </summary>
    public CameraErrorCode ErrorCode { get; }

    /// <summary>
    /// Returns a user-friendly description of the error based on the error code.
    /// </summary>
    /// <returns>A readable description.</returns>
    public string GetUserMessage()
    {
        var context = ControlName is not null ? $"{ControlName}: " : String.Empty;

        return ErrorCode switch
        {
            CameraErrorCode.InvalidArgument =>
                $"{context}The value is out of range or invalid for this camera.",
            CameraErrorCode.NotSupported =>
                ControlName is not null
                    ? $"This camera does not support {ControlName}."
                    : "This camera does not support that operation.",
            CameraErrorCode.DeviceBusy =>
                $"{context}The camera is in use by another application.",
            CameraErrorCode.PermissionDenied =>
                "Camera access was denied. Request permission before opening a camera.",
            CameraErrorCode.DeviceNotFound =>
                "The selected camera was not found. It may have been disconnected.",
            CameraErrorCode.FormatNotSupported =>
                $"{context}The requested capture format is not supported by this camera.",
            CameraErrorCode.NotRunning =>
                "The camera is not running. Open a camera before reading or setting controls.",
            CameraErrorCode.AlreadyRunning =>
                "The camera is already running. Stop it before opening again.",
            CameraErrorCode.Timeout =>
                $"{context}The camera operation timed out. The device may be unresponsive.",
            CameraErrorCode.Cancelled =>
                "The camera operation was cancelled.",
            CameraErrorCode.Platform =>
                Message,
            _ => Message
        };
    }
}
