using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ThinCam.Avalonia;
using ThinCamDemo.Services;
using ThinCamDemo.ViewModels;

namespace ThinCamDemo.Views;

/// <summary>Displays and manages the main camera demo view.</summary>
public sealed partial class MainView : UserControl, IAsyncDisposable
{
    private int _initialized;
    private int _disposed;

    /// <summary>Initializes a new main view.</summary>
    public MainView()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(DemoServices.Platform) { SaveFileHandler = SaveFileAsync };
        DataContext = ViewModel;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    /// <summary>Gets the view model displayed by this view.</summary>
    public MainViewModel ViewModel { get; }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            ViewModel.RefreshDevicesCommand.Execute(null);
    }

    private async void OnDetachedFromVisualTree(object? sender,
                                                VisualTreeAttachmentEventArgs eventArgs)
        => await DisposeAsync();

    private void OnPreviewFrameRendered(object? sender,
                                        CameraPreviewFrameEventArgs eventArgs)
    {
        if (eventArgs.HasFrame)
            ViewModel.ReportFrameRendered();
    }

    private async Task<bool> SaveFileAsync(SaveFileRequest request,
                                           CancellationToken cancellationToken)
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { CanSave: true } storageProvider)
        {
            throw new NotSupportedException("The current Avalonia platform does not provide a save-file picker.");
        }

        var extension = request.Extension.TrimStart('.');
        var fileType = new FilePickerFileType(request.Description) { Patterns = [$"*.{extension}"], MimeTypes = [request.MimeType] };

        IStorageFile? file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = request.Description, SuggestedFileName = request.SuggestedFileName,
            DefaultExtension = extension, FileTypeChoices = [fileType]
        });

        if (file is null)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        await using Stream stream = await file.OpenWriteAsync();
        await stream.WriteAsync(request.Data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        AttachedToVisualTree -= OnAttachedToVisualTree;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        await ViewModel.DisposeAsync();
    }
}
