using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace WeArchive.App.ViewModels;

/// <summary>
/// Minimal MVVM plumbing. The MVP deliberately avoids a large MVVM framework.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>Synchronous command with an explicit can-execute predicate.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Async command that keeps the UI thread free and prevents overlapping execution.
/// All long-running work is awaited rather than blocked, and the command reports failures
/// through <paramref name="onError"/> instead of letting them escape the dispatcher.
/// </summary>
public sealed class AsyncRelayCommand(
    Func<CancellationToken, Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onError = null) : ICommand
{
    private readonly Func<CancellationToken, Task> _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _isRunning;

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        _cts = new CancellationTokenSource();

        try
        {
            await _execute(_cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal outcome and is surfaced by the view model.
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
