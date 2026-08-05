using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ThinCamDemo.Views;

/// <summary>Hosts the demo's main desktop view.</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Initializes a new main window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    private MainView MainContentViewControl
        => this.FindControl<MainView>("MainContentView")
           ?? throw new InvalidOperationException("The main view was not created.");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnClosed(object? sender, EventArgs eventArgs)
    {
        Closed -= OnClosed;
        await MainContentViewControl.DisposeAsync();
    }
}
