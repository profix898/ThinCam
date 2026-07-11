using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ThinCam.Demo.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    private MainView MainContentViewControl =>
        this.FindControl<MainView>("MainContentView")
        ?? throw new InvalidOperationException("The main view was not created.");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnClosed(object? sender, EventArgs eventArgs)
    {
        Closed -= OnClosed;
        await MainContentViewControl.DisposeAsync();
    }
}
