using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MgsvModMgr.Gui.Commands;

/// <summary>
/// Minimal <see cref="ICommand"/> over a parameterless async delegate.
/// Always reports <c>CanExecute = true</c>; raise <see cref="RaiseCanExecuteChanged"/>
/// manually if the executable state ever changes.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> _execute;

    public RelayCommand(Func<Task> execute) => _execute = execute;
    public RelayCommand(Action execute)
        : this(() => { execute(); return Task.CompletedTask; }) { }

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter)
    {
        try { await _execute(); }
        catch { /* Bound view-models own their error handling. */ }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Parameterised counterpart to <see cref="RelayCommand"/>.
/// The bound command parameter is forwarded to the delegate verbatim.
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;

    public RelayCommand(Func<T?, Task> execute) => _execute = execute;
    public RelayCommand(Action<T?> execute)
        : this(o => { execute(o); return Task.CompletedTask; }) { }

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter)
    {
        try { await _execute((T?)parameter); }
        catch { /* Bound view-models own their error handling. */ }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
