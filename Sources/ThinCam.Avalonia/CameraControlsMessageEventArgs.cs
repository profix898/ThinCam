using Avalonia.Interactivity;

namespace ThinCam.Avalonia;

/// <summary>
/// Carries a user-facing message raised by <see cref="CameraControls" /> when a control
/// operation succeeds or fails.
/// </summary>
public sealed class CameraControlsMessageEventArgs : RoutedEventArgs
{
    internal CameraControlsMessageEventArgs(RoutedEvent routedEvent,
                                            string message,
                                            Exception? exception = null)
        : base(routedEvent)
    {
        Message = message;
        Exception = exception;
    }

    /// <summary>Gets the originating exception, or <see langword="null" /> for status messages.</summary>
    public Exception? Exception { get; }

    /// <summary>Gets the user-facing message.</summary>
    public string Message { get; }
}
