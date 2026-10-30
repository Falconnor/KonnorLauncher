using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Players;
using KonnorLauncher.Core.Progress;
using KonnorLauncher.Core.Versions;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Elemento visible de la lista de versiones.</summary>
public sealed record VersionItem(string? Id, string Text, bool IsSelected, bool IsSelectable);

/// <summary>
/// ViewModel de la ventana principal: bienvenida, estado/progreso, botón JUGAR y selector de versión.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    // Textos del botón JUGAR según el estado.
    private const string PlayText = "JUGAR";
    private const string VerifyingText = "VERIFICANDO";
    private const string LaunchingText = "INICIANDO JUEGO";

    // Geometría del popup de versiones: min(n, 6) × 30 + 12 (40 si está vacío).
    private const int ItemHeight = 30;
    private const int MaxVisibleItems = 6;

    private readonly LauncherSettings _settings;
    private readonly VersionService _versions;
    private readonly PlayWorkflow _workflow;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        LauncherSettings settings,
        VersionService versions,
        PlayWorkflow workflow,
        OptionsViewModel options,
        UsernamePromptViewModel usernamePrompt,
        IDialogService dialogs,
        ILogger<MainViewModel> logger)
    {
        _settings = settings;
        _versions = versions;
        _workflow = workflow;
        _dialogs = dialogs;
        _logger = logger;
        Options = options;
        UsernamePrompt = usernamePrompt;
        Options.Saved += (_, _) => RefreshWelcome(); // si cambia el nombre, se actualiza la bienvenida

        // La versión se toma del ensamblado para no tener que cambiarla a mano.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        LauncherVersionText = $"v{version?.ToString(3) ?? "1.0.0"}";

        RefreshWelcome();
        RefreshSelectedVersionLabel();
    }

    public OptionsViewModel Options { get; }
    public UsernamePromptViewModel UsernamePrompt { get; }
    public ObservableCollection<VersionItem> VersionItems { get; } = [];

    public string LauncherVersionText { get; }

    [ObservableProperty] private string _welcomeText = string.Empty;
    [ObservableProperty] private bool _isWelcomeVisible;
    [ObservableProperty] private string _actionText = string.Empty;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _playButtonText = PlayText;
    [ObservableProperty] private bool _isPlayEnabled = true;
    [ObservableProperty] private string _selectedVersionLabel = string.Empty;
    [ObservableProperty] private bool _isVersionPopupOpen;
    [ObservableProperty] private double _versionPopupHeight = 40;

    // ══════════════════════════════════════════════════════════════════
    // ARRANQUE: lista de Mojang en segundo plano
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Descarga la lista de releases de Mojang; si el popup está abierto, lo refresca.</summary>
    public async Task LoadRemoteVersionsAsync()
    {
        await Task.Run(() => _versions.FetchRemoteReleasesAsync()).ConfigureAwait(true);
        if (IsVersionPopupOpen)
            RefreshVersionItems();
    }

    // ══════════════════════════════════════════════════════════════════
    // SELECTOR DE VERSIÓN
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Cada vez que se abre el popup se reconstruye la lista.</summary>
    partial void OnIsVersionPopupOpenChanged(bool value)
    {
        if (value)
            RefreshVersionItems();
    }

    /// <summary>Reconstruye la lista: instaladas, remotas con "(Descargar ↓)" y elementos especiales.</summary>
    private void RefreshVersionItems()
    {
        VersionItems.Clear();
        var remoteLoaded = _versions.RemoteReleases is not null;
        var all = _versions.GetAllVersions();
        var selected = _versions.SelectedVersion;

        if (!remoteLoaded)
            VersionItems.Add(new VersionItem(null, "Cargando versiones oficiales...", false, false));

        if (all.Count == 0 && remoteLoaded)
        {
            VersionItems.Add(new VersionItem(null, "No hay versiones disponibles", false, false));
        }
        else
        {
            foreach (var entry in all)
            {
                var text = entry.IsInstalled ? entry.Id : $"{entry.Id}  (Descargar ↓)";
                var isSelected = entry.Id == selected;
                if (isSelected)
                    text = $"✓  {text}";
                VersionItems.Add(new VersionItem(entry.Id, text, isSelected, true));
            }
        }

        var count = VersionItems.Count;
        VersionPopupHeight = count == 0 ? 40 : Math.Min(count, MaxVisibleItems) * ItemHeight + 12;
    }

    /// <summary>Al elegir una versión se guarda al instante, se actualiza la etiqueta y se cierra el popup.</summary>
    [RelayCommand]
    private void SelectVersion(VersionItem? item)
    {
        if (item?.Id is null)
            return;
        try
        {
            _versions.SelectVersion(item.Id);
            RefreshSelectedVersionLabel();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la versión");
            _dialogs.ShowWarning("Error", $"No se pudo guardar la version:\n{ex.Message}");
        }

        IsVersionPopupOpen = false;
    }

    private void RefreshSelectedVersionLabel()
    {
        var selected = _settings.MinecraftVersion;
        SelectedVersionLabel = string.IsNullOrWhiteSpace(selected) ? "Selecciona una versión" : selected;
    }

    // ══════════════════════════════════════════════════════════════════
    // OPCIONES
    // ══════════════════════════════════════════════════════════════════

    [RelayCommand]
    private void OpenOptions() => Options.Open();

    private void RefreshWelcome()
    {
        var name = _settings.PlayerUsername;
        WelcomeText = string.IsNullOrWhiteSpace(name) ? string.Empty : $"¡Bienvenido {name}!";
        IsWelcomeVisible = WelcomeText.Length > 0 && IsPlayEnabled;
    }

    // ══════════════════════════════════════════════════════════════════
    // BOTÓN JUGAR
    // ══════════════════════════════════════════════════════════════════

    // AllowConcurrentExecutions: el estado del botón lo controla IsPlayEnabled; si no, el comando
    // lo deshabilitaría mientras el overlay de nombre está abierto.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayAsync()
    {
        if (!IsPlayEnabled)
            return;

        // 1. Sin nombre válido → overlay obligatorio; cancelar aborta todo.
        _settings.Properties.Reload();
        if (!UsernameRules.IsValid(_settings.PlayerUsername))
        {
            if (!await UsernamePrompt.ShowAsync())
                return;
            RefreshWelcome();
        }

        // 2. Estado inicial: bienvenida oculta, VERIFICANDO, botón deshabilitado, estado limpio.
        IsPlayEnabled = false;
        IsWelcomeVisible = false;
        IsVersionPopupOpen = false;
        PlayButtonText = VerifyingText;
        SetAction(string.Empty, null);

        // El Progress<T> se crea en el hilo de UI para que los reportes vuelvan a él solos.
        var uiProgress = new Progress<LauncherProgress>(OnProgress);
        var progress = new ThrottledProgress(uiProgress);

        PlayResult result;
        try
        {
            // 3. Verificar → instalar → lanzar → esperar (todo fuera del hilo de UI).
            result = await Task.Run(() => _workflow.RunAsync(progress));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado en el flujo de JUGAR");
            result = PlayResult.Fail($"Error al iniciar el juego: {ex.Message}");
        }

        // 4. Fin: botón JUGAR habilitado; mensaje de error si lo hubo.
        // (Los reportes de progreso que lleguen tarde se ignoran en OnProgress.)
        SetAction(result.Message, null);
        PlayButtonText = PlayText;
        IsPlayEnabled = true;
        RefreshSelectedVersionLabel();
        if (result.Success)
            RefreshWelcome(); // La bienvenida vuelve cuando no hay un mensaje ocupando su lugar.
    }

    /// <summary>Traduce el progreso tipado a lo que se ve en pantalla.</summary>
    private void OnProgress(LauncherProgress progress)
    {
        if (IsPlayEnabled)
            return; // Reporte tardío tras terminar el flujo: se ignora.

        switch (progress.Phase)
        {
            case LauncherPhase.Launching:
            case LauncherPhase.Running:
                PlayButtonText = LaunchingText;
                SetAction(string.Empty, null);
                break;
            default:
                SetAction(progress.Message, progress.Percent);
                break;
        }
    }

    /// <summary>Porcentaje con valor → barra visible; null → barra oculta y solo texto.</summary>
    private void SetAction(string text, double? percent)
    {
        ActionText = text.Trim();
        IsProgressVisible = percent is not null;
        if (percent is { } value)
            ProgressValue = Math.Clamp(value, 0, 100);
    }
}
