using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Players;
using KonnorLauncher.Core.Progress;
using KonnorLauncher.Core.Versions;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>
/// Ventana principal: barra lateral de instancias, panel de la instancia seleccionada y botón JUGAR.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    // Textos del botón JUGAR según el estado.
    private const string PlayText = "JUGAR";
    private const string VerifyingText = "VERIFICANDO";
    private const string InstallingText = "INSTALANDO";
    private const string LaunchingText = "EN PARTIDA";

    private readonly LauncherSettings _settings;
    private readonly VersionService _versions;
    private readonly PlayWorkflow _workflow;
    private readonly InstanceStore _store;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        LauncherSettings settings,
        VersionService versions,
        PlayWorkflow workflow,
        InstanceStore store,
        OptionsViewModel options,
        UsernamePromptViewModel usernamePrompt,
        InstanceEditorViewModel editor,
        ConfirmViewModel confirm,
        IDialogService dialogs,
        ILogger<MainViewModel> logger)
    {
        _settings = settings;
        _versions = versions;
        _workflow = workflow;
        _store = store;
        _dialogs = dialogs;
        _logger = logger;
        Options = options;
        UsernamePrompt = usernamePrompt;
        Editor = editor;
        Confirm = confirm;
        Options.Saved += (_, _) => RefreshPlayer();
        Editor.Saved += (_, instance) => ReloadInstances(instance.Id);
        Editor.DeleteRequested += async (_, instance) => await DeleteInstanceAsync(instance);

        // La versión se toma del ensamblado para no tener que cambiarla a mano.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        LauncherVersionText = $"v{version?.ToString(3) ?? "1.0.0"}";

        RefreshPlayer();
        ReloadInstances(_settings.Properties.Get(LauncherSettings.KeySelectedInstance));
    }

    public OptionsViewModel Options { get; }
    public UsernamePromptViewModel UsernamePrompt { get; }
    public InstanceEditorViewModel Editor { get; }
    public ConfirmViewModel Confirm { get; }
    public ObservableCollection<InstanceItemViewModel> Instances { get; } = [];

    public string LauncherVersionText { get; }

    [ObservableProperty] private InstanceItemViewModel? _selectedInstance;
    [ObservableProperty] private bool _hasInstances;
    [ObservableProperty] private string _playerName = string.Empty;
    [ObservableProperty] private string _playerInitials = "?";
    [ObservableProperty] private string _actionText = string.Empty;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _playButtonText = PlayText;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isPlayEnabled;

    // ══════════════════════════════════════════════════════════════════
    // ARRANQUE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Descarga la lista de releases de Mojang (para el selector de versiones del editor).</summary>
    public async Task LoadRemoteVersionsAsync()
    {
        await Task.Run(() => _versions.FetchRemoteReleasesAsync()).ConfigureAwait(true);
        Editor.NotifyRemoteVersionsLoaded();
    }

    // ══════════════════════════════════════════════════════════════════
    // INSTANCIAS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Relee las instancias del disco y selecciona <paramref name="selectId"/> (o la primera).</summary>
    private void ReloadInstances(string? selectId)
    {
        var all = _store.LoadAll();
        Instances.Clear();
        foreach (var instance in all)
            Instances.Add(new InstanceItemViewModel(instance, _store.GetDirectory(instance.Id)));
        HasInstances = Instances.Count > 0;
        SelectedInstance = Instances.FirstOrDefault(i => i.Id == selectId) ?? Instances.FirstOrDefault();
        UpdatePlayEnabled();
    }

    partial void OnSelectedInstanceChanged(InstanceItemViewModel? value)
    {
        if (value is not null && value.Id != _settings.Properties.Get(LauncherSettings.KeySelectedInstance))
            _settings.Properties.Set(LauncherSettings.KeySelectedInstance, value.Id);
        if (!IsPlaying)
            SetAction(string.Empty, null);
        UpdatePlayEnabled();
    }

    [RelayCommand]
    private void NewInstance() => Editor.OpenForNew();

    [RelayCommand]
    private void EditInstance()
    {
        if (SelectedInstance is { } selected && !IsPlaying)
            Editor.OpenForEdit(selected.Model);
    }

    /// <summary>
    /// "Eliminar instancia" (desde el editor): confirma, borra la carpeta y cierra el editor.
    /// Si se cancela, el editor sigue abierto.
    /// </summary>
    private async Task DeleteInstanceAsync(GameInstance instance)
    {
        if (IsPlaying)
            return;
        var confirmed = await Confirm.ShowAsync(
            $"Eliminar «{instance.Name}»",
            "Se borra la carpeta de la instancia con todo su contenido: mods, configuración, mundos y capturas. " +
            "Las versiones y librerías compartidas se conservan para las demás instancias. No se puede deshacer.",
            "Eliminar instancia", isDanger: true);
        if (!confirmed)
            return;
        try
        {
            _store.Delete(instance.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "No se pudo eliminar la instancia {Id}", instance.Id);
            _dialogs.ShowWarning("Error", $"No se pudo eliminar la instancia:\n{ex.Message}");
            return;
        }

        Editor.Close();
        ReloadInstances(null);
    }

    [RelayCommand]
    private void OpenInstanceFolder()
    {
        if (SelectedInstance is not { } selected)
            return;
        var dir = _store.GetDirectory(selected.Id);
        try
        {
            Directory.CreateDirectory(dir);
            _dialogs.OpenInExplorer(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "No se pudo abrir la carpeta {Dir}", dir);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // OPCIONES Y JUGADOR
    // ══════════════════════════════════════════════════════════════════

    [RelayCommand]
    private void OpenOptions() => Options.Open();

    private void RefreshPlayer()
    {
        var name = _settings.PlayerUsername.Trim();
        PlayerName = name.Length == 0 ? "Sin nombre" : name;
        PlayerInitials = name.Length == 0 ? "?" : InstanceItemViewModel.MakeInitials(name);
    }

    // ══════════════════════════════════════════════════════════════════
    // BOTÓN JUGAR
    // ══════════════════════════════════════════════════════════════════

    private void UpdatePlayEnabled() => IsPlayEnabled = !IsPlaying && SelectedInstance is not null;

    // AllowConcurrentExecutions: el estado del botón lo controla IsPlayEnabled; si no, el comando
    // lo deshabilitaría mientras el overlay de nombre está abierto.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayAsync()
    {
        if (!IsPlayEnabled || SelectedInstance is not { } selected)
            return;

        // 1. Sin nombre válido → overlay obligatorio; cancelar aborta todo.
        _settings.Properties.Reload();
        if (!UsernameRules.IsValid(_settings.PlayerUsername))
        {
            if (!await UsernamePrompt.ShowAsync())
                return;
            RefreshPlayer();
        }

        // 2. Estado inicial.
        IsPlaying = true;
        UpdatePlayEnabled();
        PlayButtonText = selected.IsSynced ? VerifyingText : InstallingText;
        SetAction(string.Empty, null);

        // El Progress<T> se crea en el hilo de UI para que los reportes vuelvan a él solos.
        var uiProgress = new Progress<LauncherProgress>(OnProgress);
        var progress = new ThrottledProgress(uiProgress);
        var instance = selected.Model;

        PlayResult result;
        try
        {
            // 3. Verificar → cargador → instalar → lanzar → esperar (todo fuera del hilo de UI).
            result = await Task.Run(() => _workflow.RunAsync(instance, progress));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado en el flujo de JUGAR");
            result = PlayResult.Fail($"Error al iniciar el juego: {ex.Message}");
        }

        // 4. Fin: botón habilitado, datos de la instancia (última partida) y mensaje si hubo error.
        IsPlaying = false;
        PlayButtonText = PlayText;
        ReloadInstances(instance.Id);
        SetAction(result.Message, null);
    }

    /// <summary>Traduce el progreso tipado a lo que se ve en pantalla.</summary>
    private void OnProgress(LauncherProgress progress)
    {
        if (!IsPlaying)
            return; // Reporte tardío tras terminar el flujo: se ignora.

        switch (progress.Phase)
        {
            case LauncherPhase.Launching:
            case LauncherPhase.Running:
                PlayButtonText = LaunchingText;
                SetAction("Minecraft está abierto. El launcher espera a que se cierre.", null);
                break;
            case LauncherPhase.Installing:
                PlayButtonText = InstallingText;
                SetAction(progress.Message, progress.Percent);
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

    /// <summary>Carpeta de datos (instancias y archivos compartidos) para el pie de la barra lateral.</summary>
    public string DataDir => LauncherPaths.DataDir;
}
