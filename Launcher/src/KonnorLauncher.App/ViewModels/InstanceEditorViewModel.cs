using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Loaders;
using KonnorLauncher.Core.Paths;
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
/// </summary>
public sealed partial class InstanceEditorViewModel : ObservableObject
{
    private readonly InstanceStore _store;
    private readonly VersionService _versions;
    private readonly LoaderVersionService _loaders;
    private readonly LauncherSettings _settings;
    private readonly ILogger<InstanceEditorViewModel> _logger;

    private GameInstance? _editing;
    private IReadOnlyList<LoaderVersionInfo> _loaderVersions = [];
    private CancellationTokenSource? _loaderLoad;
    private bool _loaderLoadFailed;

    public InstanceEditorViewModel(InstanceStore store, VersionService versions, LoaderVersionService loaders,
        LauncherSettings settings, ILogger<InstanceEditorViewModel> logger)
    {
        _store = store;
        _versions = versions;
        _loaders = loaders;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Se dispara con la instancia ya guardada.</summary>
    public event EventHandler<GameInstance>? Saved;

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
    [ObservableProperty] private bool _deleteUnauthorized = true;
    [ObservableProperty] private string? _error;

    /// <summary>"Otra": se muestra la barra con la lista de versiones del cargador.</summary>
    public bool IsSpecificChannel => Channel == LoaderChannel.Specific;

    public string SaveText => IsNew ? "Crear instancia" : "Guardar cambios";

    public string MinecraftVersionLabel => string.IsNullOrWhiteSpace(MinecraftVersion) ? "Elige una versión de Minecraft" : $"Minecraft {MinecraftVersion}";

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
        ServerUrl = _settings.ServerUrl; // la del launcher.properties de la comunidad, lista para marcar la casilla
        DeleteUnauthorized = true;
        OpenCommon();
    }

    public void OpenForEdit(GameInstance instance)
    {
        _editing = instance.Clone();
        IsNew = false;
        Title = "Editar instancia";
        Name = instance.Name;
        NameError = null;
        MinecraftVersion = instance.MinecraftVersion;
        Loader = instance.Loader;
        Channel = instance.LoaderChannel;
        SpecificLoaderVersion = instance.LoaderChannel == LoaderChannel.Specific ? instance.LoaderVersion : null;
        SyncWithServer = instance.SyncWithServer;
        ServerUrl = instance.ServerUrl;
        DeleteUnauthorized = instance.DeleteUnauthorized;
        OpenCommon();
    }

    private void OpenCommon()
    {
        Error = null;
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
        SpecificLoaderVersion = null;
        if (Channel == LoaderChannel.Specific)
            Channel = LoaderChannel.Stable;
        IsLoaderListOpen = false;
        _ = RefreshLoaderVersionsAsync();
    }

    partial void OnIsNewChanged(bool value) => OnPropertyChanged(nameof(SaveText));

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
        if (Loader == ModLoader.Vanilla || string.IsNullOrWhiteSpace(MinecraftVersion))
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

    partial void OnSyncWithServerChanged(bool value)
    {
        if (IsVersionListOpen)
            RebuildVersionOptions();
    }

    [RelayCommand]
    private void Save()
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

        if (string.IsNullOrWhiteSpace(MinecraftVersion))
        {
            Error = "Elige una versión de Minecraft.";
            return;
        }

        string? loaderVersion = null;
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

        var url = ServerUrl.Trim();
        if (SyncWithServer && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            Error = "Escribe la dirección del servidor (por ejemplo http://192.168.1.10:59381).";
            return;
        }

        var instance = _editing ?? new GameInstance();
        var changedGame = instance.MinecraftVersion != MinecraftVersion.Trim() || instance.Loader != Loader
                          || instance.LoaderVersion != loaderVersion || instance.SyncWithServer != SyncWithServer;
        instance.Name = name;
        instance.MinecraftVersion = MinecraftVersion.Trim();
        instance.Loader = Loader;
        instance.LoaderChannel = IsModded ? Channel : LoaderChannel.Stable;
        instance.LoaderVersion = loaderVersion;
        instance.SyncWithServer = SyncWithServer;
        instance.ServerUrl = url;
        instance.DeleteUnauthorized = DeleteUnauthorized;
        if (changedGame)
            instance.InstalledVersionId = null; // se volverá a resolver al jugar

        try
        {
            instance = IsNew ? _store.Create(instance) : SaveExisting(instance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogError(ex, "No se pudo guardar la instancia");
            Error = $"No se pudo guardar: {ex.Message}";
            return;
        }

        _loaderLoad?.Cancel();
        IsOpen = false;
        Saved?.Invoke(this, instance);
    }

    private GameInstance SaveExisting(GameInstance instance)
    {
        _store.Save(instance);
        return instance;
    }

    [RelayCommand]
    private void Cancel()
    {
        _loaderLoad?.Cancel();
        IsOpen = false;
    }
}
