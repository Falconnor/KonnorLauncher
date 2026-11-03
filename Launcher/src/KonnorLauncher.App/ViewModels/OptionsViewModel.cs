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
/// ViewModel del overlay "OPCIONES" (ajustes del jugador, comunes a todas las instancias). Carga los
/// valores de <c>launcher.properties</c> al abrirlo y los guarda todos juntos al pulsar GUARDAR.
/// La versión, el servidor y la carpeta del juego son de cada instancia.
/// </summary>
public sealed partial class OptionsViewModel : ObservableObject
{
    private static readonly string[] RamValues = ["1G", "2G", "3G", "4G", "6G", "8G", "10G", "12G", "16G"];

    private readonly LauncherSettings _settings;
    private readonly LauncherPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly ILogger<OptionsViewModel> _logger;

    public OptionsViewModel(LauncherSettings settings, LauncherPaths paths, IDialogService dialogs, ILogger<OptionsViewModel> logger)
    {
        _settings = settings;
        _paths = paths;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>Se dispara después de guardar (la ventana principal actualiza la bienvenida).</summary>
    public event EventHandler? Saved;

    public ObservableCollection<RamOption> RamOptions { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private RamOption? _selectedRam;
    [ObservableProperty] private string _javaPath = string.Empty;

    /// <summary>Carpeta con las instancias y los archivos compartidos.</summary>
    public string DataDir => LauncherPaths.DataDir;

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

        // Java vacío = automático (el campo muestra "Automático (recomendado)").
        JavaPath = JavaSelector.IsAutomatic(_settings.JavaPath) ? string.Empty : _settings.JavaPath;
        IsOpen = true;
    }

    /// <summary>GUARDAR: guarda todas las claves de una vez (el nombre y la skin están en la cuenta).</summary>
    [RelayCommand]
    private void Save()
    {
        _settings.Properties.SetMany(
        [
            new(LauncherSettings.KeyJavaMemory, SelectedRam?.Value ?? LauncherSettings.DefaultMemory),
            new(LauncherSettings.KeyJavaPath, JavaPath.Trim()),
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

    /// <summary>Abre en el Explorador la carpeta de datos (instancias y archivos compartidos).</summary>
    [RelayCommand]
    private void OpenDataFolder()
    {
        var path = DataDir;
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
}
