using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace PRJ2_Extractor.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandRequery.Requery += value;
        remove => CommandRequery.Requery -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandRequery.Requery += value;
        remove => CommandRequery.Requery -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        CommandRequery.Invalidate();
        try { await execute(); }
        finally
        {
            _running = false;
            CommandRequery.Invalidate();
        }
    }
}

/// <summary>
/// Asks every button bound to a command to evaluate it again (what WPF's CommandManager did on its own). Called after
/// anything that can change whether a command may run, such as a level being opened or an export starting.
/// </summary>
public static class CommandRequery
{
    public static event EventHandler? Requery;

    public static void Invalidate()
    {
        if (Dispatcher.UIThread.CheckAccess()) Requery?.Invoke(null, EventArgs.Empty);
        else Dispatcher.UIThread.Post(() => Requery?.Invoke(null, EventArgs.Empty));
    }
}
