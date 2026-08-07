using System.Windows.Input;

namespace ThinCamDemo.Infrastructure;

/// <summary>Adapts synchronous delegates to <see cref="ICommand" />.</summary>
/// <param name="execute">The action to execute.</param>
/// <param name="canExecute">An optional predicate that determines whether execution is allowed.</param>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    #region ICommand

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => execute();

    #endregion

    /// <summary>Notifies listeners that command availability may have changed.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
