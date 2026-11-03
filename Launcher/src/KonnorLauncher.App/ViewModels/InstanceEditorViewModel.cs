using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Loaders;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Progress;
using KonnorLauncher.Core.Versions;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Opción de la lista desplegable de versiones de Minecraft.</summary>
public sealed record VersionOption(string Id, string Label, bool IsSelected, bool IsSelectable = true);

/// <summary>Opción de la lista de versiones del cargador ("Otra").</summary>
public sealed record LoaderOption(string Version, string Label, bool IsSelected);

/// <summary>
/// Crear y editar instancias: nombre, versión de Minecraft (barra que se despliega), cargador de mods
/// (botones), canal del cargador (Estable · Última · Otra, con su lista) y sincronización con el
/// servidor. Al guardar resuelve la versión concreta del cargador; las carpetas y dependencias se
/// crean e instalan solas al jugar.
/// <para>
/// Con la sincronización activada, la versión y el cargador los decide el servidor: el selector se
/// bloquea y, al crear la instancia, se descargan sus archivos antes de darla por creada. Si la
/// descarga falla o se cancela, la instancia no se crea.
/// </para>
/// </summary>
public sealed partial class InstanceEditorViewModel : ObservableObject
{
    private readonly InstanceStore _store;
    private readonly InstanceSync _sync;
    private readonly VersionService _versions;
    private readonly LoaderVersionService _loaders;
    private readonly LauncherSettings _settings;
    private readonly ILogger<InstanceEditorViewModel> _logger;

    private GameInstance? _editing;
    private IReadOnlyList<LoaderVersionInfo> _loaderVersions = [];
    private CancellationTokenSource? _loaderLoad;
    private CancellationTokenSource? _syncRun;
    private bool _loaderLoadFailed;

    public InstanceEditorViewModel(InstanceStore store, InstanceSync sync, VersionService versions, LoaderVersionService loaders,
        LauncherSettings settings, ILogger<InstanceEditorViewModel> logger)
    {
        _store = store;
        _sync = sync;
        _versions = versions;
        _loaders = loaders;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Se dispara con la instancia ya guardada.</summary>
    public event EventHandler<GameInstance>? Saved;

    /// <summary>"Eliminar instancia": la ventana principal pide confirmación y la borra.</summary>
    public event EventHandler<GameInstance>? DeleteRequested;

    public ObservableCollection<VersionOption> VersionOptions { get; } = [];
    public ObservableCollection<LoaderOption> LoaderOptions { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private string _minecraftVersion = string.Empty;
    [ObservableProperty] private bool _isVersionListOpen;
    [ObservableProperty] private string _versionFilter = string.Empty;
    [ObservableProperty] private ModLoader _loader = ModLoader.Vanilla;
    [ObservableProperty] private LoaderChannel _channel = LoaderChannel.Stable;
    [ObservableProperty] private bool _isModded;
    [ObservableProperty] private bool _isLoaderListOpen;
    [ObservableProperty] private string? _specificLoaderVersion;
    [ObservableProperty] private bool _isLoadingLoaders;
    [ObservableProperty] private bool _isLoaderAvailable;
    [ObservableProperty] private string _loaderStatus = string.Empty;
    [ObservableProperty] private string _loaderListLabel = "Elige una versión";
    [ObservableProperty] private bool _syncWithServer;
    [ObservableProperty] private string _serverUrl = string.Empty;
    [ObservableProperty] private bool _installPacks;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _syncStatus = string.Empty;
    [ObservableProperty] private double _syncProgress;
    [ObservableProperty] private bool _isSyncProgressKnown;
    [ObservableProperty] private bool _customMemory;
    [ObservableProperty] private double _memoryMb = 4096;
    [ObservableProperty] private string _memoryText = string.Empty;

    // ── Memoria ──────────────────────────────────────────────────────────
    public double MemoryMinMb => SystemMemory.MinMb;
    public double MemoryMaxMb => SystemMemory.MaxMb;
    public double MemoryStepMb => SystemMemory.StepMb;
    public string MemoryRangeText => $"De {FormatMemory(SystemMemory.MinMb)} a {FormatMemory(SystemMemory.MaxMb)} (memoria instalada).";
    public string DefaultMemoryText => CustomMemory
        ? $"Solo para esta instancia. Las demás usan la de Opciones ({FormatMemory(_settings.MemoryMb)})."
        : $"Desactivado: se usa la de Opciones ({FormatMemory(_settings.MemoryMb)}).";

    /// <summary>Más del 75 % de la RAM del equipo: Windows y el resto de programas se quedan cortos.</summary>
    public bool IsMemoryHigh => CustomMemory && MemoryMb > SystemMemory.TotalMb * 0.75;

    /// <summary>"Otra": se muestra la barra con la lista de versiones del cargador.</summary>
    public bool IsSpecificChannel => Channel == LoaderChannel.Specific;

    public string SaveText => IsNew ? (SyncWithServer ? "Crear y descargar" : "Crear instancia") : "Guardar cambios";

    /// <summary>Versión y cargador se pueden elegir: no con la sincronización activada ni durante la descarga.</summary>
    public bool IsSelectionUnlocked => !SyncWithServer && !IsSyncing;

    /// <summary>La fila Estable · Última · Otra solo tiene sentido con un cargador elegido a mano.</summary>
    public bool ShowLoaderVersion => IsModded && !SyncWithServer;

    public bool IsNotSyncing => !IsSyncing;

    /// <summary>Instancia vinculada a su servidor y no liberada: sincronización y dirección bloqueadas.</summary>
    public bool IsSyncLocked => _editing?.IsSyncLocked == true;

    public bool IsSyncSwitchEnabled => !IsSyncing && !IsSyncLocked;

    /// <summary>El servidor no deja cambiar la dirección (opción reversible del servidor).</summary>
    public bool IsUrlLocked => _editing?.IsUrlLocked == true;

    public bool IsUrlEditable => !IsSyncing && !IsUrlLocked;

    /// <summary>El servidor obliga a instalar sus packs (opción reversible del servidor).</summary>
    public bool IsPacksLocked => _editing?.IsPacksLocked == true;

    public bool IsPacksSwitchEnabled => !IsSyncing && !IsPacksLocked;


    /// <summary>Explica lo que el servidor permite o bloquea en esta instancia.</summary>
    public string? SyncLockText
    {
        get
        {
            if (_editing is null || !_editing.SyncWithServer || !(_editing.SyncLocked || _editing.ServerReleased))
                return null;
            return IsSyncLocked
                ? "Instancia vinculada a su servidor: sigue sus instrucciones de seguridad."
                : "El servidor liberó esta instancia: puedes activar o desactivar la sincronización.";
        }
    }

    private void NotifyServerPolicy()
    {
        OnPropertyChanged(nameof(IsSyncLocked));
        OnPropertyChanged(nameof(IsSyncSwitchEnabled));
        OnPropertyChanged(nameof(IsUrlLocked));
        OnPropertyChanged(nameof(IsUrlEditable));
        OnPropertyChanged(nameof(SyncLockText));
        OnPropertyChanged(nameof(IsPacksLocked));
        OnPropertyChanged(nameof(IsPacksSwitchEnabled));
        if (IsPacksLocked)
            InstallPacks = true;
    }

    public string MinecraftVersionLabel => SyncWithServer
        ? (string.IsNullOrWhiteSpace(MinecraftVersion) || IsNew ? "La decide el servidor" : $"{ServerVersionText(MinecraftVersion)}  ·  del servidor")
        : string.IsNullOrWhiteSpace(MinecraftVersion) ? "Elige una versión de Minecraft" : $"Minecraft {MinecraftVersion}";

    /// <summary>
    /// "fabric-loader-0.19.3-1.21.1" (el servidor envía versions) o "1.21.1" con Fabric 0.19.3 (lo
    /// publica en el manifest) → "Minecraft 1.21.1 · Fabric 0.19.3".
    /// </summary>
    private string ServerVersionText(string id)
    {
        var (minecraft, loader, version) = GameInstance.ParseVersionId(id);
        if (loader == ModLoader.Vanilla && Loader != ModLoader.Vanilla)
            (loader, version) = (Loader, SpecificLoaderVersion);
        return loader == ModLoader.Vanilla
            ? $"Minecraft {minecraft}"
            : $"Minecraft {minecraft} · {GameInstance.LoaderDisplayName(loader)} {version}".TrimEnd();
    }

    // ══════════════════════════════════════════════════════════════════
    // ABRIR
    // ══════════════════════════════════════════════════════════════════

    public void OpenForNew()
    {
        _editing = null;
        IsNew = true;
        Title = "Nueva instancia";
        Name = string.Empty;
        NameError = null;
        MinecraftVersion = _versions.RemoteReleases?.FirstOrDefault() ?? string.Empty;
        Loader = ModLoader.Vanilla;
        Channel = LoaderChannel.Stable;
        SpecificLoaderVersion = null;
        SyncWithServer = false;
        CustomMemory = false;
        MemoryMb = ClampMemory(_settings.MemoryMb);
        ServerUrl = _settings.ServerUrl; // la del launcher.properties de la comunidad, lista para marcar la casilla
        InstallPacks = false;
        OpenCommon();
    }

    public void OpenForEdit(GameInstance instance)
    {
        _editing = instance.Clone();
        IsNew = false;
        Title = "Editar instancia";
        Name = instance.Name;
        NameError = null;
        SyncWithServer = instance.SyncWithServer; // antes que la versión: con sincronización no se consultan cargadores
        CustomMemory = instance.MemoryMb is not null;
        MemoryMb = ClampMemory(instance.MemoryMb ?? _settings.MemoryMb);
        MinecraftVersion = instance.MinecraftVersion;
        // En una instancia sincronizada se marca (bloqueado) el cargador que publica el servidor.
        Loader = instance.SyncWithServer ? instance.Display.Loader : instance.Loader;
        Channel = instance.LoaderChannel;
        SpecificLoaderVersion = instance.LoaderChannel == LoaderChannel.Specific ? instance.LoaderVersion : null;
        ServerUrl = instance.ServerUrl;
        InstallPacks = instance.InstallPacks || instance.IsPacksLocked;
        OpenCommon();
        // Ya sincronizada: se pregunta al servidor por su política actual (liberación, dirección...).
        if (instance.SyncWithServer && (instance.SyncLocked || instance.ServerReleased))
            _ = RefreshServerPolicyAsync(instance.Id);
    }

    /// <summary>
    /// Instancia ya sincronizada: pregunta al servidor por su política (liberación, siempre en línea,
    /// bloqueo de la dirección). Si cambió, se guarda y el formulario se actualiza en el acto.
    /// </summary>
    private async Task RefreshServerPolicyAsync(string id)
    {
        var probe = _editing?.Clone();
        if (probe is null || !await _sync.RefreshPolicyAsync(probe) || _editing?.Id != id || !IsOpen)
            return;
        if (!InstanceSync.PolicyDiffers(_editing, probe))
            return;

        InstanceSync.CopyPolicy(probe, _editing);
        try
        {
            // Solo cambia la política: se guarda sobre la versión en disco, no sobre el formulario.
            var stored = _store.LoadAll().FirstOrDefault(i => i.Id == id);
            if (stored is not null)
            {
                InstanceSync.CopyPolicy(probe, stored);
                _store.Save(stored);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "No se pudo guardar la política del servidor de {Id}", id);
        }

        NotifyServerPolicy();
    }

    private void OpenCommon()
    {
        NotifyServerPolicy();
        Error = null;
        IsSyncing = false;
        SyncStatus = string.Empty;
        VersionFilter = string.Empty;
        IsVersionListOpen = false;
        IsLoaderListOpen = false;
        IsOpen = true;
        _ = RefreshLoaderVersionsAsync();
    }

    /// <summary>La lista de Mojang llegó (se pide al arrancar): si la lista está abierta se rehace.</summary>
    public void NotifyRemoteVersionsLoaded()
    {
        if (IsOpen && IsNew && string.IsNullOrWhiteSpace(MinecraftVersion))
            MinecraftVersion = _versions.RemoteReleases?.FirstOrDefault() ?? string.Empty;
        if (IsVersionListOpen)
            RebuildVersionOptions();
    }

    // ══════════════════════════════════════════════════════════════════
    // VERSIÓN DE MINECRAFT
    // ══════════════════════════════════════════════════════════════════

    partial void OnIsVersionListOpenChanged(bool value)
    {
        if (value)
        {
            IsLoaderListOpen = false;
            RebuildVersionOptions();
        }
    }

    partial void OnVersionFilterChanged(string value) => RebuildVersionOptions();

    partial void OnMinecraftVersionChanged(string value)
    {
        OnPropertyChanged(nameof(MinecraftVersionLabel));
        SpecificLoaderVersion = null; // la versión concreta del cargador depende de la de Minecraft
        if (Channel == LoaderChannel.Specific)
            Channel = LoaderChannel.Stable;
        _ = RefreshLoaderVersionsAsync();
    }

    /// <summary>Instaladas de esta instancia (las que publica el servidor, por ejemplo) + releases de Mojang.</summary>
    private void RebuildVersionOptions()
    {
        VersionOptions.Clear();
        var filter = VersionFilter.Trim();
        var draft = _editing?.Clone() ?? new GameInstance { Id = "_" };
        draft.SyncWithServer = SyncWithServer;
        var installed = VersionService.GetInstalledVersions(InstanceLayout.For(draft, _store.InstancesDir, LauncherPaths.MetaDir).VersionsDir);
        List<VersionEntry> all;
        if (SyncWithServer)
        {
            // Instalada en la instancia (p. ej. el Forge que publica el servidor) + releases de Mojang.
            all = VersionService.Combine(installed, _versions.RemoteReleases);
        }
        else
        {
            // Solo versiones de Minecraft: los ids internos de los cargadores no se eligen a mano.
            var downloaded = installed.ToHashSet(StringComparer.Ordinal);
            all = (_versions.RemoteReleases ?? []).Select(id => new VersionEntry(id, downloaded.Contains(id), "release")).ToList();
            if (MinecraftVersion.Length > 0 && all.All(e => e.Id != MinecraftVersion))
                all.Insert(0, new VersionEntry(MinecraftVersion, downloaded.Contains(MinecraftVersion), "current"));
        }

        if (_versions.RemoteReleases is null)
            VersionOptions.Add(new VersionOption(string.Empty, "Cargando versiones oficiales…", false, false));
        else if (_versions.RemoteReleases.Count == 0)
            VersionOptions.Add(new VersionOption(string.Empty, "Sin conexión: solo se muestran las instaladas", false, false));

        foreach (var entry in all)
        {
            if (filter.Length > 0 && !entry.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var label = entry.IsInstalled ? $"{entry.Id}  ·  descargada" : entry.Id;
            VersionOptions.Add(new VersionOption(entry.Id, label, entry.Id == MinecraftVersion));
        }

        if (filter.Length > 0 && VersionOptions.All(o => !o.IsSelectable))
            VersionOptions.Add(new VersionOption(string.Empty, $"Ninguna versión contiene «{filter}»", false, false));
    }

    [RelayCommand]
    private void SelectVersion(VersionOption? option)
    {
        if (option is null || !option.IsSelectable)
            return;
        MinecraftVersion = option.Id;
        IsVersionListOpen = false;
    }

    // ══════════════════════════════════════════════════════════════════
    // CARGADOR DE MODS
    // ══════════════════════════════════════════════════════════════════

    partial void OnLoaderChanged(ModLoader value)
    {
        IsModded = value != ModLoader.Vanilla;
        OnPropertyChanged(nameof(ShowLoaderVersion));
        SpecificLoaderVersion = null;
        if (Channel == LoaderChannel.Specific)
            Channel = LoaderChannel.Stable;
        IsLoaderListOpen = false;
        _ = RefreshLoaderVersionsAsync();
    }

    partial void OnIsNewChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(MinecraftVersionLabel));
    }

    partial void OnChannelChanged(LoaderChannel value)
    {
        OnPropertyChanged(nameof(IsSpecificChannel));
        // "Otra" despliega la lista de versiones disponibles.
        IsLoaderListOpen = value == LoaderChannel.Specific;
        UpdateLoaderStatus();
    }

    partial void OnIsLoaderListOpenChanged(bool value)
    {
        if (value)
            IsVersionListOpen = false;
    }

    partial void OnSpecificLoaderVersionChanged(string? value)
    {
        LoaderListLabel = value is null ? "Elige una versión" : $"{GameInstance.LoaderDisplayName(Loader)} {value}";
        RebuildLoaderOptions();
        UpdateLoaderStatus();
    }

    /// <summary>Pide las versiones del cargador para la versión de Minecraft elegida (cancela la anterior).</summary>
    private async Task RefreshLoaderVersionsAsync()
    {
        _loaderLoad?.Cancel();
        _loaderVersions = [];
        _loaderLoadFailed = false;
        RebuildLoaderOptions();
        if (Loader == ModLoader.Vanilla || SyncWithServer || string.IsNullOrWhiteSpace(MinecraftVersion))
        {
            IsLoadingLoaders = false;
            UpdateLoaderStatus();
            return;
        }

        var cts = _loaderLoad = new CancellationTokenSource();
        IsLoadingLoaders = true;
        UpdateLoaderStatus();
        var (loader, mc) = (Loader, MinecraftVersion);
        try
        {
            var list = await Task.Run(() => _loaders.GetVersionsAsync(loader, mc, cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            _loaderVersions = list;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudieron obtener las versiones de {Loader} para {Minecraft}", loader, mc);
            _loaderLoadFailed = true;
        }

        IsLoadingLoaders = false;
        RebuildLoaderOptions();
        UpdateLoaderStatus();
    }

    private void RebuildLoaderOptions()
    {
        LoaderOptions.Clear();
        foreach (var v in _loaderVersions)
        {
            var label = v.IsStable ? v.Version : $"{v.Version}  ·  beta";
            LoaderOptions.Add(new LoaderOption(v.Version, label, v.Version == SpecificLoaderVersion));
        }
    }

    [RelayCommand]
    private void SelectLoaderVersion(LoaderOption? option)
    {
        if (option is null)
            return;
        SpecificLoaderVersion = option.Version;
        Channel = LoaderChannel.Specific;
        IsLoaderListOpen = false;
    }

    /// <summary>Versión concreta que se guardará según el canal elegido.</summary>
    private string? ResolvedLoaderVersion => Channel == LoaderChannel.Specific
        ? SpecificLoaderVersion
        : LoaderVersionService.Pick(_loaderVersions, Channel);

    private void UpdateLoaderStatus()
    {
        var name = GameInstance.LoaderDisplayName(Loader);
        IsLoaderAvailable = _loaderVersions.Count > 0;
        if (Loader == ModLoader.Vanilla)
            LoaderStatus = string.Empty;
        else if (string.IsNullOrWhiteSpace(MinecraftVersion))
            LoaderStatus = "Elige primero la versión de Minecraft.";
        else if (IsLoadingLoaders)
            LoaderStatus = $"Buscando versiones de {name} para {MinecraftVersion}…";
        else if (_loaderLoadFailed)
            LoaderStatus = $"No se pudieron consultar las versiones de {name}. Revisa la conexión.";
        else if (_loaderVersions.Count == 0)
            LoaderStatus = $"{name} no está disponible para Minecraft {MinecraftVersion}.";
        else if (ResolvedLoaderVersion is { } version)
        {
            var kind = Channel switch
            {
                LoaderChannel.Stable => "la versión estable",
                LoaderChannel.Latest => "la última versión",
                _ => "versión elegida",
            };
            LoaderStatus = $"Se instalará {name} {version} ({kind}).";
        }
        else
            LoaderStatus = $"Elige una versión de {name} en la lista.";
    }

    // ══════════════════════════════════════════════════════════════════
    // GUARDAR / CANCELAR
    // ══════════════════════════════════════════════════════════════════

    partial void OnNameChanged(string value) => NameError = null;

    // ══════════════════════════════════════════════════════════════════
    // MEMORIA
    // ══════════════════════════════════════════════════════════════════

    private bool _formattingMemory;

    partial void OnCustomMemoryChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMemoryHigh));
        OnPropertyChanged(nameof(DefaultMemoryText));
    }

    /// <summary>El deslizador cambió: la casilla muestra la cantidad.</summary>
    partial void OnMemoryMbChanged(double value)
    {
        _formattingMemory = true;
        MemoryText = FormatMemory((int)value);
        _formattingMemory = false;
        OnPropertyChanged(nameof(IsMemoryHigh));
    }

    /// <summary>Se escribió en la casilla: se interpreta, se ajusta al rango y mueve el deslizador.</summary>
    partial void OnMemoryTextChanged(string value)
    {
        if (_formattingMemory)
            return;
        if (ParseMemory(value) is { } mb)
            MemoryMb = ClampMemory(mb);
        // Valor no válido o fuera de rango: la casilla vuelve a mostrar el del deslizador.
        _formattingMemory = true;
        MemoryText = FormatMemory((int)MemoryMb);
        _formattingMemory = false;
    }

    private static double ClampMemory(double mb) => SystemMemory.Clamp(mb);

    public static string FormatMemory(int mb) => SystemMemory.Format(mb);

    private static int? ParseMemory(string? text) => SystemMemory.Parse(text);

    // ══════════════════════════════════════════════════════════════════
    // ELIMINAR
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Solo al editar una instancia que ya existe y sin una descarga en curso.</summary>
    [RelayCommand]
    private void RequestDelete()
    {
        if (!IsNew && !IsSyncing && _editing is not null)
            DeleteRequested?.Invoke(this, _editing);
    }

    /// <summary>Cierra el editor sin guardar (por ejemplo, tras eliminar la instancia).</summary>
    public void Close()
    {
        _loaderLoad?.Cancel();
        IsOpen = false;
    }

    partial void OnSyncWithServerChanged(bool value)
    {
        // Con la sincronización activada, versión y cargador los decide el servidor.
        if (value)
        {
            IsVersionListOpen = false;
            IsLoaderListOpen = false;
        }
        else if (GameInstance.ParseVersionId(MinecraftVersion) is { Loader: not ModLoader.Vanilla } parsed)
        {
            // Al desactivarla, el id del servidor (fabric-loader-0.19.3-1.21.1) pasa a versión + cargador.
            MinecraftVersion = parsed.Minecraft;
            Loader = parsed.Loader;
        }
        else
        {
            _ = RefreshLoaderVersionsAsync();
        }

        OnPropertyChanged(nameof(IsSelectionUnlocked));
        OnPropertyChanged(nameof(ShowLoaderVersion));
        OnPropertyChanged(nameof(MinecraftVersionLabel));
        OnPropertyChanged(nameof(SaveText));
    }

    partial void OnIsSyncingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSyncSwitchEnabled));
        OnPropertyChanged(nameof(IsUrlEditable));
        OnPropertyChanged(nameof(IsPacksSwitchEnabled));
        OnPropertyChanged(nameof(IsSelectionUnlocked));
        OnPropertyChanged(nameof(IsNotSyncing));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = null;
        string name;
        try
        {
            name = InstanceStore.ValidateName(Name);
        }
        catch (ArgumentException ex)
        {
            NameError = ex.Message;
            return;
        }

        var url = ServerUrl.Trim();
        string? loaderVersion = null;
        if (SyncWithServer)
        {
            if (!(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            {
                Error = "Escribe la dirección del servidor (por ejemplo http://192.168.1.10:59381).";
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(MinecraftVersion))
            {
                Error = "Elige una versión de Minecraft.";
                return;
            }

            if (IsModded)
            {
                if (IsLoadingLoaders)
                {
                    Error = "Espera a que terminen de cargar las versiones del cargador.";
                    return;
                }

                loaderVersion = ResolvedLoaderVersion;
                if (loaderVersion is null)
                {
                    Error = LoaderStatus;
                    return;
                }
            }
        }

        // Defensa: lo que el servidor bloquea no se puede guardar aunque se fuerce la interfaz.
        if (_editing is { IsSyncLocked: true } && !SyncWithServer)
        {
            Error = "Esta instancia está vinculada a su servidor: solo el servidor puede liberarla.";
            return;
        }

        if (_editing is { IsUrlLocked: true } urlLocked && urlLocked.ServerUrl != url)
        {
            Error = "El servidor no permite cambiar su dirección en esta instancia.";
            return;
        }

        var instance = _editing ?? new GameInstance();
        // Hay que descargar del servidor al crear, al activar la sincronización o al cambiar de servidor.
        // Lo que el servidor obliga se mantiene aunque se fuerce la interfaz.
        var installPacks = InstallPacks || (_editing?.IsPacksLocked == true);
        // Hay que descargar del servidor al crear, al activar la sincronización, al cambiar de servidor
        // o al pedir sus packs.
        var needsSync = SyncWithServer && (IsNew || !instance.SyncWithServer || instance.ServerUrl != url
                                           || string.IsNullOrWhiteSpace(instance.MinecraftVersion)
                                           || (installPacks && !instance.InstallPacks));
        var changedGame = SyncWithServer != instance.SyncWithServer
                          || (!SyncWithServer && (instance.MinecraftVersion != MinecraftVersion.Trim() || instance.Loader != Loader
                                                  || instance.LoaderVersion != loaderVersion));
        instance.Name = name;
        instance.MemoryMb = CustomMemory ? (int)ClampMemory(MemoryMb) : null;
        instance.SyncWithServer = SyncWithServer;
        instance.ServerUrl = url;
        instance.InstallPacks = installPacks;
        if (SyncWithServer)
        {
            // La versión la decide el servidor (se adopta al sincronizar); al editar una instancia ya
            // sincronizada se conserva la que tiene.
            if (changedGame)
            {
                instance.Loader = ModLoader.Vanilla;
                instance.LoaderChannel = LoaderChannel.Stable;
                instance.LoaderVersion = null;
            }
        }
        else
        {
            instance.MinecraftVersion = MinecraftVersion.Trim();
            instance.Loader = Loader;
            instance.LoaderChannel = IsModded ? Channel : LoaderChannel.Stable;
            instance.LoaderVersion = loaderVersion;
        }

        if (changedGame)
            instance.InstalledVersionId = null; // se volverá a resolver al jugar

        var created = false;
        try
        {
            if (IsNew)
            {
                instance = _store.Create(instance);
                created = true;
            }

            if (needsSync && !await DownloadFromServerAsync(instance))
            {
                // La creación no se da por concluida: no queda una instancia a medias.
                if (created)
                    TryDelete(instance.Id);
                return;
            }

            _store.Save(instance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogError(ex, "No se pudo guardar la instancia");
            if (created)
                TryDelete(instance.Id);
            Error = $"No se pudo guardar: {ex.Message}";
            return;
        }

        _loaderLoad?.Cancel();
        IsOpen = false;
        Saved?.Invoke(this, instance);
    }

    /// <summary>
    /// Descarga el contenido del servidor en la carpeta de la instancia y adopta la versión que publica.
    /// Devuelve false (con el motivo en <see cref="Error"/>) si falla o se cancela.
    /// </summary>
    private async Task<bool> DownloadFromServerAsync(GameInstance instance)
    {
        var cts = _syncRun = new CancellationTokenSource();
        IsSyncing = true;
        IsSyncProgressKnown = false;
        SyncStatus = "Conectando con el servidor…";
        var progress = new ThrottledProgress(new Progress<LauncherProgress>(p =>
        {
            if (!IsSyncing)
                return;
            if (!string.IsNullOrWhiteSpace(p.Message) && p.Message != KonnorLauncher.Core.Verification.VerificationResult.SuccessStatus)
                SyncStatus = char.ToUpperInvariant(p.Message[0]) + p.Message[1..];
            IsSyncProgressKnown = p.Percent is not null;
            if (p.Percent is { } percent)
                SyncProgress = percent;
        }));

        InstanceSyncResult result;
        try
        {
            result = await Task.Run(() => _sync.SyncAsync(instance, _store.InstancesDir, progress, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = InstanceSyncResult.Fail(IsNew ? "Descarga cancelada: la instancia no se creó." : "Descarga cancelada.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sincronizando la instancia {Id}", instance.Id);
            result = InstanceSyncResult.Fail($"No se pudo descargar el contenido del servidor: {ex.Message}");
        }
        finally
        {
            IsSyncing = false;
            _syncRun = null;
        }

        if (!result.Success)
        {
            Error = result.Message;
            return false;
        }

        InstanceSync.AdoptServerVersion(instance, result);
        // Mismo orden que al abrir: versión, cargador y por último la versión concreta del cargador.
        MinecraftVersion = instance.MinecraftVersion;
        Loader = instance.Display.Loader;
        Channel = instance.LoaderChannel;
        SpecificLoaderVersion = instance.LoaderChannel == LoaderChannel.Specific ? instance.LoaderVersion : null;
        OnPropertyChanged(nameof(MinecraftVersionLabel));
        _logger.LogInformation("Instancia {Id} lista con la versión del servidor {Version}", instance.Id, instance.Summary);
        return true;
    }

    private void TryDelete(string id)
    {
        try
        {
            _store.Delete(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "No se pudo borrar la instancia a medio crear {Id}", id);
        }
    }

    /// <summary>Durante la descarga, Cancelar la detiene (la instancia no se crea); si no, cierra.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsSyncing)
        {
            _syncRun?.Cancel();
            return;
        }

        _loaderLoad?.Cancel();
        IsOpen = false;
    }
}
