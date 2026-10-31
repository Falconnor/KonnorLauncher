using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Players;

namespace KonnorLauncher.App.ViewModels;

/// <summary>
/// ViewModel del overlay "ESCRIBE TU NOMBRE": aparece al pulsar JUGAR sin un nombre válido.
/// <see cref="ShowAsync"/> devuelve true si se guardó (el flujo continúa) o false si se canceló (no se juega).
/// </summary>
public sealed partial class UsernamePromptViewModel : ObservableObject
{
    private readonly LauncherSettings _settings;
    private TaskCompletionSource<bool>? _pending;

    public UsernamePromptViewModel(LauncherSettings settings) => _settings = settings;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string? _error;

    /// <summary>Muestra el overlay y espera a que el jugador guarde o cancele.</summary>
    public Task<bool> ShowAsync()
    {
        _pending?.TrySetResult(false);
        _pending = new TaskCompletionSource<bool>();
        Username = string.Empty;
        Error = null;
        IsOpen = true;
        return _pending.Task;
    }

    /// <summary>GUARDAR: valida el nombre y lo guarda en <c>player.username</c>.</summary>
    [RelayCommand]
    private void Save()
    {
        var name = Username.Trim();
        Error = UsernameRules.ValidateForPrompt(name);
        if (Error is not null)
            return;

        _settings.Properties.Set(LauncherSettings.KeyPlayerUsername, name);
        Close(true);
    }

    /// <summary>CANCELAR (o Esc): no se juega.</summary>
    [RelayCommand]
    private void Cancel() => Close(false);

    private void Close(bool result)
    {
        IsOpen = false;
        _pending?.TrySetResult(result);
        _pending = null;
    }
}
