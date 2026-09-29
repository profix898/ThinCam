using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ThinCamDemo.Services;
using ThinCamDemo.ViewModels;
using ThinCamDemo.Views;

// Android implicit usings pull in Android.App.Application, so disambiguate here.
using Application = Avalonia.Application;

namespace ThinCamDemo;

/// <summary>Defines the ThinCam demo application.</summary>
public sealed class App : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = CreateMainViewModel() };
        else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
            activityLifetime.MainViewFactory = () => new MainView { DataContext = CreateMainViewModel() };
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new MainView { DataContext = CreateMainViewModel() };

        base.OnFrameworkInitializationCompleted();
    }

    private static MainViewModel CreateMainViewModel() => new MainViewModel(DemoServices.Platform);
}
