using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Launching;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Players;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Opción del combo de RAM: se muestra "4 GB" y se guarda "4G".</summary>
public sealed record RamOption(string Display, string Value)
{
    /// <summary>El cuadro del combo muestra este texto ("4 GB").</summary>
    public override string ToString() => Display;
}

/// <summary>
/// ViewModel del overlay "OPCIONES". Carga los valores de <c>launcher.properties</c> al abrirlo
/// y los guarda todos juntos al pulsar GUARDAR.
/// </summary>
public sealed partial class OptionsViewModel : ObservableObject
{
    private static readonly string[] RamValues = ["1G", "2G", "3G", "4G", "6G", "8G", "10G", "12G", "16G"];

    private readonly LauncherSettings _settings;
    private readonly LauncherPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly ILogger<OptionsViewModel> _logger;
    private string _originalGamePath = string.Empty;

    public OptionsViewModel(LauncherSettings settings, LauncherPaths paths, IDialogService dialogs, ILogger<OptionsViewModel> logger)
    {
        _settings = settings;
        _paths = paths;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>Se dispara después de guardar (la ventana principal actualiza la bienvenida).</summary>
    public event EventHandler? Saved;

    /// <summary>La vista enfoca el campo de nombre cuando GUARDAR falla por un nombre inválido.</summary>
    public event EventHandler? FocusUsernameRequested;

    public ObservableCollection<RamOption> RamOptions { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private RamOption? _selectedRam;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string? _usernameError;
    [ObservableProperty] private string _javaPath = string.Empty;
    [ObservableProperty] private string _serverUrl = string.Empty;
    [ObservableProperty] private bool _verifyOnStart;
    [ObservableProperty] private string _gamePath = string.Empty;
    [ObservableProperty] private string _skinPath = string.Empty;

    /// <summary>Valida el nombre mientras se escribe.</summary>
    partial void OnUsernameChanged(string value) => UsernameError = UsernameRules.ValidateForOptions(value);

    /// <summary>Abre el panel con los valores actuales del archivo.</summary>
    public void Open()
    {
        _settings.Properties.Reload();

        // RAM: si el valor guardado no está en la lista (p. ej. "5G"), se añade para no perderlo al guardar.
        RamOptions.Clear();
        foreach (var value in RamValues)
            RamOptions.Add(new RamOption(value.Replace("G", " GB"), value));
        var currentRam = _settings.JavaMemory.Trim();
        var selected = RamOptions.FirstOrDefault(o => string.Equals(o.Value, currentRam, StringComparison.OrdinalIgnoreCase));
        if (selected is null && LauncherSettings.ParseMemoryMb(currentRam) is not null)
        {
            selected = new RamOption(currentRam, currentRam);
            RamOptions.Add(selected);
        }

        SelectedRam = selected ?? RamOptions.First(o => o.Value == LauncherSettings.DefaultMemory);

        Username = _settings.PlayerUsername;
        UsernameError = null; // El error solo aparece al editar o al guardar.

        // Java vacío = automático (el campo muestra "Automático (recomendado)").
        JavaPath = JavaSelector.IsAutomatic(_settings.JavaPath) ? string.Empty : _settings.JavaPath;
        ServerUrl = _settings.ServerUrl;
        VerifyOnStart = _settings.EnableVerify;

        // Si la carpeta está vacía, se muestra la ruta que se usa por defecto.
        _originalGamePath = _settings.GamePath;
        GamePath = string.IsNullOrWhiteSpace(_originalGamePath) ? _paths.ResolveGameDir(null) : _originalGamePath;
        SkinPath = _settings.PlayerSkin;
        IsOpen = true;
    }

    /// <summary>GUARDAR: valida el nombre y guarda todas las claves de una vez.</summary>
    [RelayCommand]
    private void Save()
    {
        var username = Username.Trim();
        if (!UsernameRules.IsValid(username))
        {
            // El mensaje se calcula aquí también por si el campo no se llegó a editar.
            UsernameError = UsernameRules.ValidateForOptions(username);
            FocusUsernameRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Si no se cambió la carpeta por defecto mostrada, se deja vacía (= por defecto).
        var gamePath = GamePath.Trim();
        if (string.IsNullOrWhiteSpace(_originalGamePath) && string.Equals(gamePath, _paths.ResolveGameDir(null), StringComparison.OrdinalIgnoreCase))
            gamePath = string.Empty;

        _settings.Properties.SetMany(
        [
            new(LauncherSettings.KeyPlayerUsername, username),
            new(LauncherSettings.KeyJavaMemory, SelectedRam?.Value ?? LauncherSettings.DefaultMemory),
            new(LauncherSettings.KeyJavaPath, JavaPath.Trim()),
            new(LauncherSettings.KeyServerUrl, ServerUrl.Trim()),
            new(LauncherSettings.KeyGamePath, gamePath),
            new(LauncherSettings.KeyEnableVerify, VerifyOnStart ? "true" : "false"),
            new(LauncherSettings.KeyPlayerSkin, SkinPath.Trim()),
        ]);
        _logger.LogInformation("Opciones guardadas");
        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>CANCELAR, ✕ o Esc: cierra sin guardar.</summary>
    [RelayCommand]
    private void Cancel() => IsOpen = false;

    /// <summary>"..." de Java: selector de ejecutable.</summary>
    [RelayCommand]
    private void BrowseJava()
    {
        var path = _dialogs.PickFile("Seleccionar Java", "Ejecutables (*.exe)|*.exe|Todos (*)|*.*");
        if (path is not null)
            JavaPath = path;
    }

    /// <summary>"..." de la carpeta del juego.</summary>
    [RelayCommand]
    private void BrowseGamePath()
    {
        var folder = _dialogs.PickFolder("Seleccionar carpeta de Minecraft");
        if (folder is not null)
            GamePath = folder;
    }

    /// <summary>📁: crea la carpeta si no existe y la abre en el Explorador (una ruta relativa es relativa al launcher).</summary>
    [RelayCommand]
    private void OpenGameFolder()
    {
        var path = _paths.ResolveUserFolder(GamePath);
        if (path is null)
            return;
        try
        {
            Directory.CreateDirectory(path);
            _dialogs.OpenInExplorer(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "No se pudo abrir la carpeta {Path}", path);
        }
    }

    /// <summary>"..." de skin: selector de PNG.</summary>
    [RelayCommand]
    private void BrowseSkin()
    {
        var path = _dialogs.PickFile("Seleccionar Skin", "Imágenes PNG (*.png)|*.png");
        if (path is not null)
            SkinPath = path;
    }

    /// <summary>👁: vista previa (si no hay skin válida, muestra a Steve).</summary>
    [RelayCommand]
    private void PreviewSkin()
    {
        var path = SkinPath.Trim();
        _dialogs.ShowSkinPreview(path.Length > 0 && File.Exists(path) ? path : null);
    }
}
