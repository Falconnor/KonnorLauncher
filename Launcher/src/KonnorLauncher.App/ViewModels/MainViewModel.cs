using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Modrinth;
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
    private readonly InstanceSync _sync;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        LauncherSettings settings,
        VersionService versions,
        PlayWorkflow workflow,
        InstanceStore store,
        InstanceSync sync,
        OptionsViewModel options,
        AccountViewModel account,
        BrowseViewModel browse,
        SkinsViewModel skins,
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
        _sync = sync;
        _dialogs = dialogs;
        _logger = logger;
        Options = options;
        Account = account;
        Browse = browse;
        Skins = skins;
        UsernamePrompt = usernamePrompt;
        Editor = editor;
        Confirm = confirm;
        Options.Saved += (_, _) => RefreshPlayer();
        Account.Saved += (_, _) => RefreshPlayer();
        // Un modpack instalado crea una instancia: aparece en la lista sin salir de la página.
        Browse.InstanceCreated += (_, _) => ReloadInstances(SelectedInstance?.Id);
        Browse.InstallDialog.InstanceCreated += (_, _) => ReloadInstances(SelectedInstance?.Id);
        // Contenido instalado: se actualizan los contadores (mods, paquetes...) de esa instancia.
        Browse.InstanceChanged += (_, id) =>
        {
            if (Instances.FirstOrDefault(i => i.Id == id) is { } item)
                item.Update(item.Model);
        };
        Editor.Saved += (_, instance) => ReloadInstances(instance.Id);
        // Contador de la barra lateral: instancias con actualización.
        Instances.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.OfType<InstanceItemViewModel>() ?? [])
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(InstanceItemViewModel.HasUpdate))
                        NotifyUpdates();
                };
            NotifyUpdates();
        };
        Editor.DeleteRequested += async (_, instance) => await DeleteInstanceAsync(instance);

        // Barra lateral: Mods, Skins (en lugar de Modpacks), Paquetes de recursos y Shaders.
        SidebarLinks =
        [
            new SidebarLink(Browse.Tabs.First(t => t.Type == ContentType.Mod)),
            new SidebarLink("Skins", ""),
            new SidebarLink(Browse.Tabs.First(t => t.Type == ContentType.ResourcePack)),
            new SidebarLink(Browse.Tabs.First(t => t.Type == ContentType.Shader)),
        ];
        foreach (var tab in Browse.Tabs)
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BrowseTab.IsActive))
                    UpdateSidebar();
            };

        // La versión se toma del ensamblado para no tener que cambiarla a mano.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        LauncherVersionText = $"v{version?.ToString(3) ?? "1.0.0"}";

        RefreshPlayer();
        ReloadInstances(_settings.Properties.Get(LauncherSettings.KeySelectedInstance));
    }

    public OptionsViewModel Options { get; }
    public AccountViewModel Account { get; }
    public BrowseViewModel Browse { get; }
    public SkinsViewModel Skins { get; }
    public UsernamePromptViewModel UsernamePrompt { get; }
    public InstanceEditorViewModel Editor { get; }
    public ConfirmViewModel Confirm { get; }
    public ObservableCollection<InstanceItemViewModel> Instances { get; } = [];

    public string LauncherVersionText { get; }

    [ObservableProperty] private InstanceItemViewModel? _selectedInstance;
    [ObservableProperty] private bool _hasInstances;

    /// <summary>Se está viendo "Explorar contenido" en lugar de la instancia.</summary>
    [ObservableProperty] private bool _isBrowsing;

    /// <summary>Se está viendo la sección Skins.</summary>
    [ObservableProperty] private bool _isSkins;

    /// <summary>Se ve otra página (Explorar o Skins) en lugar de la instancia.</summary>
    public bool IsAwayFromInstance => IsBrowsing || IsSkins;

    /// <summary>Título de la cabecera: el de la página que se ve.</summary>
    public string ViewTitle => IsSkins ? "Skins" : IsBrowsing ? "Explorar" : string.Empty;

    private bool _reloading;

    public bool ShowInstancePanel => HasInstances && !IsAwayFromInstance;

    /// <summary>Instancias con una actualización del servidor pendiente (contador de la barra lateral).</summary>
    public int UpdateCount => Instances.Count(i => i.HasUpdate);

    public bool HasUpdates => UpdateCount > 0;

    private void NotifyUpdates()
    {
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(HasUpdates));
    }

    public bool ShowEmptyPanel => !HasInstances && !IsAwayFromInstance;
    [ObservableProperty] private string _playerName = string.Empty;
    [ObservableProperty] private string _playerInitials = "?";
    [ObservableProperty] private System.Windows.Media.ImageSource? _playerAvatar;
    [ObservableProperty] private bool _hasPlayerAvatar;
    [ObservableProperty] private string _actionText = string.Empty;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _playButtonText = PlayText;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isPlaying;

    /// <summary>Sin una partida en marcha (el menú de la instancia solo deja jugar o eliminar así).</summary>
    public bool IsIdle => !IsPlaying;
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

    /// <summary>
    /// Consulta el manifest de las instancias sincronizadas (sin descargar archivos): así se sabe ya
    /// al abrir si un servidor cambió su política o publicó una versión nueva del cliente.
    /// </summary>
    public async Task CheckLinkedInstancesAsync()
    {
        IReadOnlyList<GameInstance> updated;
        try
        {
            updated = await Task.Run(() => _sync.CheckLinkedInstancesAsync(_store)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudieron consultar los servidores de las instancias");
            return;
        }

        foreach (var instance in updated)
        {
            // La que se está jugando se relee al terminar la partida.
            if (IsPlaying && SelectedInstance?.Id == instance.Id)
                continue;
            Instances.FirstOrDefault(i => i.Id == instance.Id)?.Update(instance);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // INSTANCIAS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Relee las instancias del disco y selecciona <paramref name="selectId"/> (o la primera).</summary>
    private void ReloadInstances(string? selectId)
    {
        _reloading = true; // recargar la lista no es elegir una instancia: no se sale de "Explorar"
        try
        {
            var all = _store.LoadAll();
            Instances.Clear();
            foreach (var instance in all)
                Instances.Add(new InstanceItemViewModel(instance, _store.GetDirectory(instance.Id)));
            HasInstances = Instances.Count > 0;
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == selectId) ?? Instances.FirstOrDefault();
        }
        finally
        {
            _reloading = false;
        }

        UpdatePlayEnabled();
    }

    partial void OnHasInstancesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInstancePanel));
        OnPropertyChanged(nameof(ShowEmptyPanel));
    }

    partial void OnIsBrowsingChanged(bool value)
    {
        if (value)
            IsSkins = false;
        OnViewChanged();
    }

    partial void OnIsSkinsChanged(bool value)
    {
        if (value)
            IsBrowsing = false;
        OnViewChanged();
    }

    private void OnViewChanged()
    {
        OnPropertyChanged(nameof(ShowInstancePanel));
        OnPropertyChanged(nameof(ShowEmptyPanel));
        OnPropertyChanged(nameof(IsAwayFromInstance));
        OnPropertyChanged(nameof(ViewTitle));
        UpdateSidebar();
    }

    // ══════════════════════════════════════════════════════════════════
    // EXPLORAR CONTENIDO (Modrinth)
    // ══════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task OpenBrowseAsync()
    {
        IsBrowsing = true;
        await Browse.OpenAsync();
    }

    /// <summary>Accesos de la barra lateral: pestañas de Explorar y la sección Skins.</summary>
    public IReadOnlyList<SidebarLink> SidebarLinks { get; }

    /// <summary>Marca en la barra lateral la página que se está viendo.</summary>
    private void UpdateSidebar()
    {
        foreach (var link in SidebarLinks)
            link.IsCurrent = link.Tab is { } tab ? IsBrowsing && tab.IsActive : IsSkins;
    }

    [RelayCommand]
    private async Task OpenSidebarLinkAsync(SidebarLink? link)
    {
        if (link is null)
            return;
        if (link.Tab is { } tab)
        {
            IsBrowsing = true;
            await Browse.OpenAsync(tab.Type);
            return;
        }

        IsSkins = true;
        await Skins.OpenAsync();
    }

    [RelayCommand]
    private void CloseBrowse()
    {
        if (IsBrowsing)
            Browse.CloseTopmost();
        IsBrowsing = false;
        IsSkins = false;
    }

    partial void OnSelectedInstanceChanged(InstanceItemViewModel? value)
    {
        // Elegir una instancia en la barra lateral vuelve a su panel.
        if (!_reloading && value is not null)
        {
            IsBrowsing = false;
            IsSkins = false;
        }
        if (value is not null && value.Id != _settings.Properties.Get(LauncherSettings.KeySelectedInstance))
            _settings.Properties.Set(LauncherSettings.KeySelectedInstance, value.Id);
        if (!IsPlaying)
            SetAction(string.Empty, null);
        UpdatePlayEnabled();
    }

    [RelayCommand]
    private void NewInstance() => Editor.OpenForNew();

    /// <summary>Edita <paramref name="item"/> (desde el menú de la instancia) o la seleccionada.</summary>
    [RelayCommand]
    private void EditInstance(InstanceItemViewModel? item)
    {
        if ((item ?? SelectedInstance) is { } selected && !IsPlaying)
            Editor.OpenForEdit(selected.Model);
    }

    // ── Menú de la instancia (clic derecho en la lista) ─────────────────────

    /// <summary>Jugar: la selecciona (vuelve a su panel) y juega como con el botón JUGAR.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PlayInstanceAsync(InstanceItemViewModel? item)
    {
        if (item is null || IsPlaying)
            return;
        SelectedInstance = item;
        CloseBrowse();
        await PlayAsync();
    }

    /// <summary>Eliminar: pide confirmación como el botón del editor.</summary>
    [RelayCommand]
    private Task DeleteInstanceFromMenuAsync(InstanceItemViewModel? item) =>
        item is null ? Task.CompletedTask : DeleteInstanceAsync(item.Model);

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

    /// <summary>Abre la carpeta de <paramref name="item"/> (desde el menú de la instancia) o de la seleccionada.</summary>
    [RelayCommand]
    private void OpenInstanceFolder(InstanceItemViewModel? item)
    {
        if ((item ?? SelectedInstance) is not { } selected)
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
        PlayerAvatar = PlayerImages.OpenAvatar(_settings.PlayerAvatar);
        HasPlayerAvatar = PlayerAvatar is not null;
    }

    /// <summary>Zona del jugador en la barra lateral: abre la cuenta (nombre, foto y skin).</summary>
    [RelayCommand]
    private void OpenAccount() => Account.Open();

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

/// <summary>Acceso de la barra lateral: una pestaña de Explorar o (sin pestaña) la sección Skins.</summary>
public sealed partial class SidebarLink : ObservableObject
{
    public SidebarLink(BrowseTab tab)
    {
        Tab = tab;
        Label = tab.Label;
        Icon = tab.Icon;
    }

    public SidebarLink(string label, string icon)
    {
        Label = label;
        Icon = icon;
    }

    public BrowseTab? Tab { get; }
    public string Label { get; }
    public string Icon { get; }

    /// <summary>Es la página que se está viendo.</summary>
    [ObservableProperty] private bool _isCurrent;
}
