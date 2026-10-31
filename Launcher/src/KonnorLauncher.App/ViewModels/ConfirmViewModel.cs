using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Diálogo de confirmación propio del launcher (sustituye a MessageBox en acciones destructivas).</summary>
public sealed partial class ConfirmViewModel : ObservableObject
{
    private TaskCompletionSource<bool>? _pending;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _confirmText = "Aceptar";
    [ObservableProperty] private bool _isDanger;

    /// <summary>Muestra el diálogo y devuelve true si se confirma.</summary>
    public Task<bool> ShowAsync(string title, string message, string confirmText, bool isDanger)
    {
        _pending?.TrySetResult(false);
        _pending = new TaskCompletionSource<bool>();
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        IsDanger = isDanger;
        IsOpen = true;
        return _pending.Task;
    }

    [RelayCommand]
    private void Confirm() => Close(true);

    [RelayCommand]
    private void Cancel() => Close(false);

    private void Close(bool result)
    {
        IsOpen = false;
        _pending?.TrySetResult(result);
        _pending = null;
    }
}
