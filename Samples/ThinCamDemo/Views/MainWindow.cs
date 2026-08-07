using System.Diagnostics;
using Avalonia.Controls;

namespace ThinCamDemo.Views;

/// <summary>Hosts the demo's main desktop view.</summary>
public sealed partial class MainWindow : Window
{
    private Task? _shutdownTask;
    private bool _shutdownComplete;

    /// <summary>Initializes a new main window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private MainView MainContentViewControl
        => MainContentView
           ?? throw new InvalidOperationException("The main view was not created.");

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_shutdownComplete)
            return;

        // Camera shutdown is asynchronous, so cancel the first close and re-close once it completes.
        eventArgs.Cancel = true;
        _shutdownTask ??= ShutdownAsync();

        try
        {
            await _shutdownTask.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Could not shut down the main window: {exception}");
        }

        if (_shutdownComplete)
            return;

        _shutdownComplete = true;
        Closing -= OnClosing;
        Close();
    }

    private async Task ShutdownAsync() => await MainContentViewControl.DisposeAsync().ConfigureAwait(true);
}
