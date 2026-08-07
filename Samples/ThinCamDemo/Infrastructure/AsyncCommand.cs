using System.Windows.Input;

namespace ThinCamDemo.Infrastructure;

/// <summary>Adapts a cancellable asynchronous operation to <see cref="ICommand" />.</summary>
public sealed class AsyncCommand : ObservableObject, ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onException;
    private CancellationTokenSource? _executionCancellation;
    private Task _executionTask = Task.CompletedTask;
    private bool _isRunning;

    /// <summary>Initializes a new asynchronous command.</summary>
    /// <param name="execute">The asynchronous operation to execute.</param>
    /// <param name="canExecute">An optional predicate that determines whether execution is allowed.</param>
    /// <param name="onException">An optional handler for otherwise unhandled execution failures.</param>
    public AsyncCommand(Func<CancellationToken, Task> execute,
                        Func<bool>? canExecute = null,
                        Action<Exception>? onException = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onException = onException;
    }

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

    #region ICommand

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter) => await ExecuteAsync(parameter).ConfigureAwait(true);

    #endregion

    /// <summary>Executes the command and returns its completion task.</summary>
    /// <param name="parameter">The command parameter.</param>
    /// <returns>The active execution, or a completed task when execution is unavailable.</returns>
    public Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
            return Task.CompletedTask;

        var cancellation = new CancellationTokenSource();
        _executionCancellation = cancellation;
        IsRunning = true;
        _executionTask = ExecuteCoreAsync(cancellation);
        return _executionTask;
    }

    private async Task ExecuteCoreAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await _execute(cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_onException is null)
                throw;
            _onException(exception);
        }
        finally
        {
            if (ReferenceEquals(_executionCancellation, cancellation))
                _executionCancellation = null;
            cancellation.Dispose();
            IsRunning = false;
        }
    }

    /// <summary>Cancels the current execution, if any.</summary>
    public void Cancel() => _executionCancellation?.Cancel();

    /// <summary>Cancels the current execution and returns a task for that execution only.</summary>
    /// <returns>A task that completes after the execution active at call time has stopped.</returns>
    /// <remarks>
    /// The execution is captured synchronously so a later execution started while the caller
    /// awaits the returned task is neither cancelled nor awaited.
    /// </remarks>
    public Task CancelAsync()
    {
        var execution = _executionTask;
        try
        {
            _executionCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The execution completed and released its cancellation source.
        }

        return execution;
    }

    /// <summary>Notifies listeners that command availability may have changed.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
