using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ThinCam.Avalonia;
using ThinCam.Demo.Services;
using ThinCam.Demo.ViewModels;

namespace ThinCam.Demo.Views;

public sealed partial class MainView : UserControl, IAsyncDisposable
{
    private readonly MainViewModel _viewModel;
    private int _initialized;
    private int _disposed;

    public MainView()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(DemoServices.Platform)
        {
            SaveFileHandler = SaveFileAsync
        };
        DataContext = _viewModel;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public MainViewModel ViewModel => _viewModel;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            _viewModel.RefreshDevicesCommand.Execute(null);
        }
    }

    private async void OnDetachedFromVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs eventArgs) =>
        await DisposeAsync();

    private void OnPreviewFrameRendered(
        object? sender,
        CameraPreviewFrameEventArgs eventArgs)
    {
        if (eventArgs.HasFrame)
        {
            _viewModel.ReportFrameRendered();
        }
    }

    private async Task<bool> SaveFileAsync(
        SaveFileRequest request,
        CancellationToken cancellationToken)
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { CanSave: true } storageProvider)
        {
            throw new NotSupportedException(
                "The current Avalonia platform does not provide a save-file picker.");
        }

        string extension = request.Extension.TrimStart('.');
        var fileType = new FilePickerFileType(request.Description)
        {
            Patterns = [$"*.{extension}"],
            MimeTypes = [request.MimeType]
        };

        IStorageFile? file = await storageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = request.Description,
                SuggestedFileName = request.SuggestedFileName,
                DefaultExtension = extension,
                FileTypeChoices = [fileType]
            });

        if (file is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using Stream stream = await file.OpenWriteAsync();
        await stream.WriteAsync(request.Data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        AttachedToVisualTree -= OnAttachedToVisualTree;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        await _viewModel.DisposeAsync();
    }
}
