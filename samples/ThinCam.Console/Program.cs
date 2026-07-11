using ThinCam;

CameraPermissionStatus permission = CameraPermissions.GetStatus();
if (permission != CameraPermissionStatus.Granted)
{
    permission = await CameraPermissions.RequestAsync();
}

if (permission != CameraPermissionStatus.Granted)
{
    Console.Error.WriteLine($"Camera permission is {permission}.");
    return;
}

IReadOnlyList<CameraDevice> devices = CameraDevices.Enumerate();
if (devices.Count == 0)
{
    Console.Error.WriteLine("No camera devices were found.");
    return;
}

CameraDevice device = devices.FirstOrDefault(static item => item.IsDefault) ?? devices[0];
Console.WriteLine($"Opening {device.Name} ({device.Id})");

await using Camera camera = await Camera.OpenAsync(device, new CameraOpenOptions
{
    Width = 640,
    Height = 480,
    FramesPerSecond = 30,
    QueueCapacity = 2
});

CameraCapabilities capabilities = await camera.GetCapabilitiesAsync();
PrintCapabilities(capabilities);

await ApplyRequestedControlsAsync(camera, capabilities, args);

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
int count = 0;

try
{
    await foreach (VideoFrame frame in camera.GetFramesAsync(cancellation.Token))
    {
        using (frame)
        {
            Console.WriteLine(
                $"#{++count}: {frame.Width}x{frame.Height}, " +
                $"stride={frame.Stride}, bytes={frame.DataLength}, timestamp={frame.Timestamp}");
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine($"Captured {count} frames.");
}

static void PrintCapabilities(CameraCapabilities capabilities)
{
    Console.WriteLine($"Exposure modes: {Join(capabilities.Exposure.Modes)}");
    Console.WriteLine($"Exposure compensation: {FormatRange(capabilities.Exposure.CompensationEv, "EV")}");
    Console.WriteLine($"Exposure duration: {FormatRange(capabilities.Exposure.Duration, string.Empty)}");
    Console.WriteLine($"ISO: {FormatRange(capabilities.Exposure.Iso, string.Empty)}");
    Console.WriteLine($"Focus modes: {Join(capabilities.Focus.Modes)}");
    Console.WriteLine($"Manual focus: {FormatRange(capabilities.Focus.ManualPosition, string.Empty)}");
    Console.WriteLine($"Zoom: {FormatRange(capabilities.Zoom.Factor, "x")}");
    Console.WriteLine(
        $"Light: available={capabilities.Light.IsAvailable}, " +
        $"variable={capabilities.Light.SupportsVariableLevel}, " +
        $"level={FormatRange(capabilities.Light.Level, string.Empty)}");
}

static async Task ApplyRequestedControlsAsync(
    Camera camera,
    CameraCapabilities capabilities,
    string[] arguments)
{
    double? exposureEv = ReadDouble(arguments, "--exposure-ev=");
    if (exposureEv is not null)
    {
        EnsureInRange(capabilities.Exposure.CompensationEv, exposureEv.Value, "exposure compensation");
        await camera.Controls.Exposure.SetCompensationAsync(exposureEv.Value);
    }

    double? focus = ReadDouble(arguments, "--focus=");
    if (focus is not null)
    {
        EnsureInRange(capabilities.Focus.ManualPosition, focus.Value, "manual focus");
        await camera.Controls.Focus.SetPositionAsync(focus.Value);
    }

    double? zoom = ReadDouble(arguments, "--zoom=");
    if (zoom is not null)
    {
        EnsureInRange(capabilities.Zoom.Factor, zoom.Value, "zoom");
        await camera.Controls.Zoom.SetFactorAsync(zoom.Value);
    }

    bool enableLight = arguments.Contains("--light", StringComparer.OrdinalIgnoreCase);
    double? lightLevel = ReadDouble(arguments, "--light-level=");
    if (enableLight || lightLevel is not null)
    {
        if (!capabilities.Light.IsAvailable)
        {
            throw new InvalidOperationException("The selected camera does not provide a controllable light.");
        }
        if (lightLevel is not null)
        {
            EnsureInRange(capabilities.Light.Level, lightLevel.Value, "light level");
            await camera.Controls.Light.SetLevelAsync(lightLevel.Value);
        }
        else
        {
            await camera.Controls.Light.SetEnabledAsync(true);
        }
    }
}

static double? ReadDouble(string[] arguments, string prefix)
{
    string? argument = arguments.FirstOrDefault(
        item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    if (argument is null) return null;

    string text = argument[prefix.Length..];
    if (!double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double value) || !double.IsFinite(value))
    {
        throw new ArgumentException($"Invalid numeric option: {argument}");
    }
    return value;
}

static void EnsureInRange(
    NumericRange<double>? range,
    double value,
    string name)
{
    if (range is null)
    {
        throw new InvalidOperationException($"The selected camera does not support {name}.");
    }
    if (value < range.Minimum || value > range.Maximum)
    {
        throw new ArgumentOutOfRangeException(
            name,
            value,
            $"Expected a value between {range.Minimum} and {range.Maximum}.");
    }
}

static string Join<T>(IEnumerable<T> values)
{
    string text = string.Join(", ", values.Select(
        static value => value is null ? string.Empty : value.ToString()));
    return text.Length > 0 ? text : "not supported";
}

static string FormatRange<T>(NumericRange<T>? range, string suffix) =>
    range is null
        ? "not supported"
        : $"{range.Minimum}{suffix} .. {range.Maximum}{suffix} " +
          $"(default {range.Default}{suffix}, step {range.Step}{suffix})";
