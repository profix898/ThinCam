using System.Windows.Input;

namespace ThinCamDemo.Infrastructure;

/// <summary>Adapts a cancellable asynchronous operation to <see cref="ICommand" />.</summary>
public sealed class AsyncCommand : ObservableObject, ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private CancellationTokenSource? _executionCancellation;
    private bool _isRunning;

    /// <summary>Initializes a new asynchronous command.</summary>
    /// <param name="execute">The asynchronous operation to execute.</param>
    /// <param name="canExecute">An optional predicate that determines whether execution is allowed.</param>
    public AsyncCommand(Func<CancellationToken, Task> execute,
                        Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>Gets whether the command is currently executing.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
                RaiseCanExecuteChanged();
        }
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        // ICommand cannot return a Task, so track cancellation and running state around the awaited delegate.
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

    /// <summary>Cancels the current execution, if any.</summary>
    public void Cancel() => _executionCancellation?.Cancel();

    /// <summary>Notifies listeners that command availability may have changed.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
