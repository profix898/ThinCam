using System.Globalization;
using ThinCam;

var permission = CameraPermissions.GetStatus();
if (permission != CameraPermissionStatus.Granted)
    permission = await CameraPermissions.RequestAsync();

if (permission != CameraPermissionStatus.Granted)
{
    Console.Error.WriteLine($"Camera permission is {permission}.");
    return;
}

var devices = CameraDevices.Enumerate();
if (devices.Count == 0)
{
    Console.Error.WriteLine("No camera devices were found.");
    return;
}

var device = devices.FirstOrDefault(static item => item.IsDefault) ?? devices[0];
Console.WriteLine($"Opening {device.Name} ({device.Id})");

await using var camera = await Camera.OpenAsync(device, new CameraOpenOptions { Width = 640, Height = 480, FramesPerSecond = 30, QueueCapacity = 2 });

var capabilities = await camera.GetCapabilitiesAsync();
PrintCapabilities(capabilities);

await ApplyRequestedControlsAsync(camera, capabilities, args);

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var count = 0;

try
{
    await foreach (var frame in camera.GetFramesAsync(cancellation.Token))
    {
        using (frame)
        {
            Console.WriteLine($"#{++count}: {frame.Width}x{frame.Height}, " +
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
    Console.WriteLine($"Exposure duration: {FormatRange(capabilities.Exposure.Duration, String.Empty)}");
    Console.WriteLine($"ISO: {FormatRange(capabilities.Exposure.Iso, String.Empty)}");
    Console.WriteLine($"Focus modes: {Join(capabilities.Focus.Modes)}");
    Console.WriteLine($"Manual focus: {FormatRange(capabilities.Focus.ManualPosition, String.Empty)}");
    Console.WriteLine($"Zoom: {FormatRange(capabilities.Zoom.Factor, "x")}");
    Console.WriteLine($"Light: available={capabilities.Light.IsAvailable}, " +
                      $"variable={capabilities.Light.SupportsVariableLevel}, " +
                      $"level={FormatRange(capabilities.Light.Level, String.Empty)}");
}

static async Task ApplyRequestedControlsAsync(Camera camera,
                                              CameraCapabilities capabilities,
                                              string[] arguments)
{
    var exposureEv = ReadDouble(arguments, "--exposure-ev=");
    if (exposureEv is not null)
    {
        EnsureInRange(capabilities.Exposure.CompensationEv, exposureEv.Value, "exposure compensation");
        await camera.Controls.Exposure.SetCompensationAsync(exposureEv.Value);
    }

    var focus = ReadDouble(arguments, "--focus=");
    if (focus is not null)
    {
        EnsureInRange(capabilities.Focus.ManualPosition, focus.Value, "manual focus");
        await camera.Controls.Focus.SetPositionAsync(focus.Value);
    }

    var zoom = ReadDouble(arguments, "--zoom=");
    if (zoom is not null)
    {
        EnsureInRange(capabilities.Zoom.Factor, zoom.Value, "zoom");
        await camera.Controls.Zoom.SetFactorAsync(zoom.Value);
    }

    var enableLight = arguments.Contains("--light", StringComparer.OrdinalIgnoreCase);
    var lightLevel = ReadDouble(arguments, "--light-level=");
    if (enableLight || lightLevel is not null)
    {
        if (!capabilities.Light.IsAvailable)
            throw new InvalidOperationException("The selected camera does not provide a controllable light.");
        if (lightLevel is not null)
        {
            EnsureInRange(capabilities.Light.Level, lightLevel.Value, "light level");
            await camera.Controls.Light.SetLevelAsync(lightLevel.Value);
        }
        else
            await camera.Controls.Light.SetEnabledAsync(true);
    }
}

static double? ReadDouble(string[] arguments, string prefix)
{
    var argument = arguments.FirstOrDefault(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    if (argument is null)
        return null;

    var text = argument[prefix.Length..];
    if (!Double.TryParse(text,
                         NumberStyles.Float,
                         CultureInfo.InvariantCulture,
                         out var value) || !Double.IsFinite(value))
        throw new ArgumentException($"Invalid numeric option: {argument}");
    return value;
}

static void EnsureInRange(NumericRange<double>? range,
                          double value,
                          string name)
{
    if (range is null)
        throw new InvalidOperationException($"The selected camera does not support {name}.");
    if (value < range.Minimum || value > range.Maximum)
    {
        throw new ArgumentOutOfRangeException(name,
                                              value,
                                              $"Expected a value between {range.Minimum} and {range.Maximum}.");
    }
}

static string Join<T>(IEnumerable<T> values)
{
    var text = String.Join(", ", values.Select(static value => value is null ? String.Empty : value.ToString()));
    return text.Length > 0 ? text : "not supported";
}

static string FormatRange<T>(NumericRange<T>? range, string suffix)
    => range is null
        ? "not supported"
        : $"{range.Minimum}{suffix} .. {range.Maximum}{suffix} " +
          $"(default {range.Default}{suffix}, step {range.Step}{suffix})";
