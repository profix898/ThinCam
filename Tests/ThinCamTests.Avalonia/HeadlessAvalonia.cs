using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;

namespace ThinCamTests.Avalonia;

/// <summary>
/// Runs a headless Avalonia instance on a single dedicated thread and marshals test bodies onto
/// it. Avalonia is thread-affine, so every test must run on the thread that owns the dispatcher.
/// </summary>
public static class HeadlessAvalonia
{
    private static readonly BlockingCollection<(Action Work, TaskCompletionSource Completion)> Queue = new();
    private static Thread? _thread;

    /// <summary>Starts the headless Avalonia thread. Blocks until Avalonia is initialized.</summary>
    public static void Start()
    {
        if (_thread is not null)
            return;

        var ready = new TaskCompletionSource();
        _thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<ThemeTestApplication>()
                          .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                          .SetupWithoutStarting();
                ready.SetResult();
            }
            catch (Exception exception)
            {
                ready.SetException(exception);
                return;
            }

            foreach (var (work, completion) in Queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }
        }) { IsBackground = true, Name = "Headless Avalonia" };

        _thread.Start();
        ready.Task.Wait(TimeSpan.FromSeconds(60));
    }

    /// <summary>Stops accepting further work.</summary>
    public static void Stop() => Queue.CompleteAdding();

    /// <summary>Runs <paramref name="work" /> on the Avalonia thread and rethrows any failure.</summary>
    public static void Run(Action work)
    {
        var completion = new TaskCompletionSource();
        Queue.Add((work, completion));
        completion.Task.GetAwaiter().GetResult();
    }
}
