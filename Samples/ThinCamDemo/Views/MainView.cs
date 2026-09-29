using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using ThinCam.Avalonia;
using ThinCamDemo.Services;
using ThinCamDemo.ViewModels;

// Aliased because Android.App.Application is also in scope for the net10.0-android target.
using AvaloniaApplication = Avalonia.Application;

namespace ThinCamDemo.Views;

/// <summary>Displays and manages the main camera demo view.</summary>
public sealed partial class MainView : UserControl, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new SemaphoreSlim(1, 1);
    private IActivatableLifetime? _activatableLifetime;
    private int _initialized;
    private int _disposed;

    /// <summary>Initializes a new main view.</summary>
    public MainView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Gets the view model displayed by this view.</summary>
    public MainViewModel ViewModel
        => DataContext as MainViewModel
           ?? throw new InvalidOperationException("MainView requires a MainViewModel data context.");

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            AttachedToVisualTree -= OnAttachedToVisualTree;
            DetachedFromVisualTree -= OnDetachedFromVisualTree;
            DataContextChanged -= OnDataContextChanged;
            UnsubscribeFromApplicationLifetime();
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.SaveFileHandler = null;
                await viewModel.DisposeAsync().ConfigureAwait(true);
            }
            DataContext = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    #endregion

    private async void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            SubscribeToApplicationLifetime();
            SyncThemeSelector();
            ViewModel.Activate();
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
                await ViewModel.RefreshDevicesCommand.ExecuteAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Could not activate the main view: {exception}");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async void OnDetachedFromVisualTree(object? sender,
                                                VisualTreeAttachmentEventArgs eventArgs)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (Volatile.Read(ref _disposed) == 0 && DataContext is MainViewModel viewModel)
                await viewModel.DeactivateAsync().ConfigureAwait(true);
            UnsubscribeFromApplicationLifetime();
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Could not deactivate the main view: {exception}");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SaveFileHandler = SaveFileAsync;
    }

    private void SubscribeToApplicationLifetime()
    {
        if (_activatableLifetime is not null)
            return;
        _activatableLifetime = AvaloniaApplication.Current?.TryGetFeature<IActivatableLifetime>();
        if (_activatableLifetime is null)
            return;
        _activatableLifetime.Activated += OnApplicationActivated;
        _activatableLifetime.Deactivated += OnApplicationDeactivated;
    }

    private void UnsubscribeFromApplicationLifetime()
    {
        if (_activatableLifetime is null)
            return;
        _activatableLifetime.Activated -= OnApplicationActivated;
        _activatableLifetime.Deactivated -= OnApplicationDeactivated;
        _activatableLifetime = null;
    }

    private void OnApplicationActivated(object? sender, ActivatedEventArgs eventArgs)
    {
        if (eventArgs.Kind == ActivationKind.Background && Volatile.Read(ref _disposed) == 0)
            ViewModel.Activate();
    }

    private async void OnApplicationDeactivated(object? sender, ActivatedEventArgs eventArgs)
    {
        if (eventArgs.Kind != ActivationKind.Background)
            return;
        try
        {
            await ViewModel.DeactivateAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Could not suspend camera capture: {exception}");
        }
    }

    private void OnPreviewFrameRendered(object? sender,
                                        CameraPreviewFrameEventArgs eventArgs)
    {
        if (eventArgs.HasFrame)
            ViewModel.ReportFrameRendered();
    }

    private void OnControlError(object? sender, CameraControlsMessageEventArgs eventArgs) => ViewModel.Log($"ERROR: {eventArgs.Message}");

    private void OnControlStatus(object? sender, CameraControlsMessageEventArgs eventArgs) => ViewModel.Log(eventArgs.Message);

    private void OnThemeSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (AvaloniaApplication.Current is not { } application)
            return;

        application.RequestedThemeVariant = ThemeSelector.SelectedIndex switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private void SyncThemeSelector()
    {
        var variant = AvaloniaApplication.Current?.RequestedThemeVariant;
        ThemeSelector.SelectedIndex = variant switch
        {
            not null when variant == ThemeVariant.Light => 1,
            not null when variant == ThemeVariant.Dark => 2,
            _ => 0
        };
    }

    private async Task<bool> SaveFileAsync(SaveFileRequest request,
                                           CancellationToken cancellationToken)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { CanSave: true } storageProvider)
            throw new NotSupportedException("The current Avalonia platform does not provide a save-file picker.");

        var extension = request.Extension.TrimStart('.');
        var fileType = new FilePickerFileType(request.Description) { Patterns = [$"*.{extension}"], MimeTypes = [request.MimeType] };

        cancellationToken.ThrowIfCancellationRequested();
        using var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = request.Description, SuggestedFileName = request.SuggestedFileName, DefaultExtension = extension,
            FileTypeChoices = [fileType]
        });

        if (file is null)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
            stream.SetLength(0);
        await stream.WriteAsync(request.Data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return true;
    }
}
