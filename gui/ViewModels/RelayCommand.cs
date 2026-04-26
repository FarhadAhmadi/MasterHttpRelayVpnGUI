using System;
using System.Windows.Input;

namespace MasterRelayVPN.ViewModels;

public class RelayCommand : ICommand
{
    readonly Action<object?> _exec;
    readonly Predicate<object?>? _can;

    public RelayCommand(Action exec, Func<bool>? can = null)
        : this(_ => exec(), can == null ? null : _ => can()) { }

    public RelayCommand(Action<object?> exec, Predicate<object?>? can = null)
    {
        _exec = exec ?? throw new ArgumentNullException(nameof(exec));
        _can = can;
    }

    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _exec(p);

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
