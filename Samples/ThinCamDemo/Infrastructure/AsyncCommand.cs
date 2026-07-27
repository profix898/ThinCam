using System.Windows.Input;

namespace ThinCam.Demo.Infrastructure;

public sealed class AsyncCommand : ObservableObject, ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private CancellationTokenSource? _executionCancellation;
    private bool _isRunning;

    public AsyncCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanExecute(object? parameter) =>
        !IsRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _executionCancellation = cancellation;
        IsRunning = true;

        try
        {
            await _execute(cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _executionCancellation = null;
            IsRunning = false;
        }
    }

    public void Cancel() => _executionCancellation?.Cancel();

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
