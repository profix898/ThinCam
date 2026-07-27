namespace ThinCam;

public sealed class CameraException : Exception
{
    public CameraException(CameraErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public CameraException(CameraErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public CameraErrorCode ErrorCode { get; }
}
