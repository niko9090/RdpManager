using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RdpManager.Linux.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Comando semplice; <see cref="RaiseCanExecuteChanged"/> va chiamato quando cambia lo stato da cui dipende.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>Se restituisce vero il comando è spento (per esempio con l'app bloccata): non si può eseguire in nessun modo.</summary>
    public Func<bool>? Disabled { get; set; }

    public bool CanExecute(object? parameter) => !(Disabled?.Invoke() ?? false) && (_canExecute?.Invoke() ?? true);

    public void Execute(object? parameter)
    {
        if (Disabled?.Invoke() ?? false)
            return;
        _execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
