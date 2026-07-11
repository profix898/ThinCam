using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ThinCam.Interop;

namespace ThinCam;

public static partial class CameraPermissions
{
    public static CameraPermissionStatus GetStatus()
    {
#if ANDROID
        return GetAndroidStatus();
#else
        NativeHelpers.EnsureAbi();
        NativeStatus result = NativeMethods.GetPermissionStatus(out NativePermissionStatus status);
        if (result != NativeStatus.Ok) throw NativeHelpers.Exception(result);
        return (CameraPermissionStatus)(int)status;
#endif
    }

    public static async ValueTask<CameraPermissionStatus> RequestAsync(
        CancellationToken cancellationToken = default)
    {
#if ANDROID
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return GetAndroidStatus() == CameraPermissionStatus.Granted
            ? CameraPermissionStatus.Granted
            : CameraPermissionStatus.HostActionRequired;
#else
        cancellationToken.ThrowIfCancellationRequested();
        NativeHelpers.EnsureAbi();
        var completion = new TaskCompletionSource<CameraPermissionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new PermissionRequest(completion);
        GCHandle handle = GCHandle.Alloc(request);

        unsafe
        {
            NativeStatus result = NativeMethods.RequestPermission(
                (nint)(delegate* unmanaged[Cdecl]<NativePermissionStatus, nint, void>)&OnPermissionCompleted,
                GCHandle.ToIntPtr(handle));

            if (result != NativeStatus.Ok)
            {
                handle.Free();
                throw NativeHelpers.Exception(result);
            }
        }

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
#endif
    }

#if !ANDROID
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPermissionCompleted(NativePermissionStatus status, nint userData)
    {
        GCHandle handle = default;
        try
        {
            handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is PermissionRequest request)
            {
                request.Completion.TrySetResult((CameraPermissionStatus)(int)status);
            }
        }
        catch
        {
            // Exceptions must never cross an unmanaged callback boundary.
        }
        finally
        {
            try
            {
                if (handle.IsAllocated) handle.Free();
            }
            catch
            {
                // A malformed callback context must not escape to native code.
            }
        }
    }

    private sealed class PermissionRequest(
        TaskCompletionSource<CameraPermissionStatus> completion)
    {
        internal TaskCompletionSource<CameraPermissionStatus> Completion { get; } = completion;
    }
#endif
}
