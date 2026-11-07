using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Loaders;
using KonnorLauncher.Core.Modrinth;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Instancia que se puede elegir como destino.</summary>
public sealed partial class InstanceChoice(GameInstance instance, bool installed) : ObservableObject
{
    public GameInstance Instance { get; } = instance;
    public string Name => Instance.Name;
    public string Summary => Instance.Summary;
    public string Initials => InstanceItemViewModel.MakeInitials(Instance.Name);
    public bool IsInstalled { get; } = installed;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Opción seleccionable (versión de Minecraft o cargador) de "Nueva instancia".</summary>
public sealed partial class ChoiceOption<T>(T value, string label) : ObservableObject
{
    public T Value { get; } = value;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// "¿Dónde instalar?": al pulsar Instalar se elige una instancia compatible (las vinculadas a un servidor
/// no, porque su contenido lo decide el servidor) o se crea una nueva, solo con las versiones de
/// Minecraft y los cargadores para los que el proyecto publica versiones.
/// </summary>
public sealed partial class InstallDialogViewModel : ObservableObject
{
    private static readonly ModLoader[] ModLoaders = [ModLoader.Fabric, ModLoader.Forge, ModLoader.NeoForge, ModLoader.Quilt];

    private readonly ModrinthClient _client;
    private readonly InstanceStore _store;
    private readonly LoaderVersionService _loaderVersions;
    private readonly ILogger<InstallDialogViewModel> _logger;

    private TaskCompletionSource<GameInstance?>? _result;
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _loaderLoad;
    private ContentType _type;
    private List<ProjectVersion> _versions = [];

    public InstallDialogViewModel(ModrinthClient client, InstanceStore store, LoaderVersionService loaderVersions,
        ILogger<InstallDialogViewModel> logger)
    {
        _client = client;
        _store = store;
        _loaderVersions = loaderVersions;
        _logger = logger;
    }

    /// <summary>Se creó una instancia nueva (la ventana la añade a la lista).</summary>
    public event EventHandler<GameInstance>? InstanceCreated;

    public ObservableCollection<InstanceChoice> Instances { get; } = [];
    public ObservableCollection<ChoiceOption<string>> MinecraftVersions { get; } = [];
    public ObservableCollection<ChoiceOption<ModLoader>> Loaders { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isNewMode;
    [ObservableProperty] private bool _canCreate = true;
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private bool _useLatestLoader;
    [ObservableProperty] private string _loaderStatus = string.Empty;
    [ObservableProperty] private string? _resolvedLoaderVersion;

    public bool IsExistingMode
    {
        get => !IsNewMode;
        set => IsNewMode = !value;
    }

    public bool HasInstances => Instances.Count > 0;
    public bool ShowLoaders => Loaders.Count > 0;
    public string? SelectedVersion => MinecraftVersions.FirstOrDefault(v => v.IsSelected)?.Value;
    public ModLoader? SelectedLoader => Loaders.FirstOrDefault(l => l.IsSelected)?.Value;
    public string ConfirmText => IsNewMode ? "Crear e instalar" : "Instalar";

    public string NoInstancesText => _type == ContentType.Mod
        ? "Ninguna de tus instancias tiene una versión de Minecraft y un cargador para los que haya versión de este mod. Crea una nueva."
        : "Ninguna de tus instancias tiene una versión de Minecraft para la que haya versión. Crea una nueva.";

    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Abre el diálogo y devuelve la instancia elegida (o la recién creada), o null si se cancela.
    /// Con <paramref name="versionId"/> solo cuenta esa versión del proyecto.
    /// </summary>
    public Task<GameInstance?> ShowAsync(ProjectCard card, string? versionId)
    {
        _result?.TrySetResult(null);
        _result = new TaskCompletionSource<GameInstance?>();
        _load?.Cancel();
        _load = new CancellationTokenSource();
        _type = card.Type;
        Title = $"Instalar {card.Title}";
        Subtitle = "Cargando las versiones disponibles…";
        Error = null;
        Instances.Clear();
        MinecraftVersions.Clear();
        Loaders.Clear();
        LoaderStatus = string.Empty;
        ResolvedLoaderVersion = null;
        UseLatestLoader = false;
        // Un data pack va en un mundo: una instancia nueva todavía no tiene ninguno.
        CanCreate = _type != ContentType.DataPack;
        IsNewMode = false;
        NewName = card.Title.Length > InstanceStore.MaxNameLength ? card.Title[..InstanceStore.MaxNameLength].TrimEnd() : card.Title;
        IsOpen = true;
        _ = LoadAsync(card.Hit.ProjectId, versionId, _load.Token);
        return _result.Task;
    }

    private async Task LoadAsync(string projectId, string? versionId, CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var versions = versionId is null
                ? await _client.GetVersionsAsync(projectId, ct: ct)
                : [await _client.GetVersionAsync(versionId, ct)];
            ct.ThrowIfCancellationRequested();
            _versions = versions.Where(v => v.PrimaryFile is not null && Accepts(v)).ToList();
            BuildInstances(projectId);
            BuildMinecraftVersions();
            Subtitle = versionId is null
                ? "Elige una instancia o crea una nueva."
                : $"Versión {versions[0].VersionNumber}: elige una instancia o crea una nueva.";
            if (Instances.Count == 0 && CanCreate)
                IsNewMode = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (ModrinthException ex)
        {
            Error = ex.Message;
            Subtitle = string.Empty;
        }

        IsLoading = false;
    }

    /// <summary>Versiones que valen para este tipo (un mod con varios tipos publica también data packs o plugins).</summary>
    private bool Accepts(ProjectVersion version) => _type switch
    {
        ContentType.Mod => version.Loaders.Any(l => ParseLoader(l) is not null),
        ContentType.DataPack => version.Loaders.Contains("datapack"),
        ContentType.ResourcePack => version.Loaders.Contains("minecraft") || version.Loaders.Count == 0,
        _ => true,
    };

    private static ModLoader? ParseLoader(string name) => name.ToLowerInvariant() switch
    {
        "fabric" => ModLoader.Fabric,
        "forge" => ModLoader.Forge,
        "neoforge" => ModLoader.NeoForge,
        "quilt" => ModLoader.Quilt,
        _ => null,
    };

    /// <summary>¿Hay alguna versión para esta versión de Minecraft y este cargador?</summary>
    private bool Supports(string minecraft, ModLoader loader) => _versions.Any(v =>
        v.GameVersions.Contains(minecraft)
        && (_type != ContentType.Mod
            || v.Loaders.Any(l => ParseLoader(l) == loader || (loader == ModLoader.Quilt && ParseLoader(l) == ModLoader.Fabric))));

    // ── Instancias existentes ───────────────────────────────────────────

    private void BuildInstances(string projectId)
    {
        Instances.Clear();
        foreach (var instance in _store.LoadAll())
        {
            // Las vinculadas a un servidor no: su contenido lo decide el servidor.
            if (instance.SyncWithServer || ContentInstaller.BlockReason(instance, _type) is not null)
                continue;
            if (ContentInstaller.GameVersion(instance) is not { } minecraft || !Supports(minecraft, instance.Display.Loader))
                continue;
            var installed = false;
            try
            {
                installed = ContentInstaller.InstalledProjects(_store.GetDirectory(instance.Id)).Contains(projectId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            Instances.Add(new InstanceChoice(instance, installed));
        }

        if ((Instances.FirstOrDefault(i => !i.IsInstalled) ?? Instances.FirstOrDefault()) is { } first)
            first.IsSelected = true;
        OnPropertyChanged(nameof(HasInstances));
        OnPropertyChanged(nameof(NoInstancesText));
    }

    [RelayCommand]
    private void SelectInstance(InstanceChoice choice)
    {
        foreach (var item in Instances)
            item.IsSelected = item == choice;
    }

    // ── Instancia nueva ─────────────────────────────────────────────────

    private void BuildMinecraftVersions()
    {
        MinecraftVersions.Clear();
        var versions = _versions.SelectMany(v => v.GameVersions)
            .Where(v => ReleaseVersion().IsMatch(v))
            .Distinct()
            .OrderByDescending(v => System.Version.TryParse(v.Count(c => c == '.') == 0 ? v + ".0" : v, out var parsed) ? parsed : new Version(0, 0));
        foreach (var version in versions)
            MinecraftVersions.Add(new ChoiceOption<string>(version, version));
        if (MinecraftVersions.FirstOrDefault() is { } latest)
            ChooseVersion(latest);
    }

    [RelayCommand]
    private void ChooseVersion(ChoiceOption<string> option)
    {
        foreach (var item in MinecraftVersions)
            item.IsSelected = item == option;
        OnPropertyChanged(nameof(SelectedVersion));
        BuildLoaders();
    }

    /// <summary>Un mod: solo los cargadores para los que hay versión. El resto: vanilla o cualquier cargador.</summary>
    private void BuildLoaders()
    {
        var previous = SelectedLoader;
        Loaders.Clear();
        if (SelectedVersion is not { } minecraft)
            return;
        if (_type != ContentType.Mod)
            Loaders.Add(new ChoiceOption<ModLoader>(ModLoader.Vanilla, "Vanilla"));
        foreach (var loader in ModLoaders.Where(l => _type != ContentType.Mod || Supports(minecraft, l)))
            Loaders.Add(new ChoiceOption<ModLoader>(loader, GameInstance.LoaderDisplayName(loader)));
        var keep = Loaders.FirstOrDefault(l => l.Value == previous) ?? Loaders.FirstOrDefault();
        if (keep is not null)
            ChooseLoader(keep);
        OnPropertyChanged(nameof(ShowLoaders));
    }

    [RelayCommand]
    private void ChooseLoader(ChoiceOption<ModLoader> option)
    {
        foreach (var item in Loaders)
            item.IsSelected = item == option;
        OnPropertyChanged(nameof(SelectedLoader));
        _ = ResolveLoaderVersionAsync();
    }

    partial void OnUseLatestLoaderChanged(bool value) => _ = ResolveLoaderVersionAsync();

    /// <summary>Versión del cargador que se usará (la estable o la última), como al crear una instancia.</summary>
    private async Task ResolveLoaderVersionAsync()
    {
        _loaderLoad?.Cancel();
        var cts = _loaderLoad = new CancellationTokenSource();
        ResolvedLoaderVersion = null;
        if (SelectedLoader is not { } loader || SelectedVersion is not { } minecraft)
            return;
        if (loader == ModLoader.Vanilla)
        {
            LoaderStatus = $"Minecraft {minecraft} sin cargador de mods.";
            return;
        }

        LoaderStatus = $"Buscando versiones de {GameInstance.LoaderDisplayName(loader)}…";
        try
        {
            var versions = await _loaderVersions.GetVersionsAsync(loader, minecraft, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            ResolvedLoaderVersion = LoaderVersionService.Pick(versions, UseLatestLoader ? LoaderChannel.Latest : LoaderChannel.Stable);
            LoaderStatus = ResolvedLoaderVersion is { } v
                ? $"Se instalará {GameInstance.LoaderDisplayName(loader)} {v} para Minecraft {minecraft}."
                : $"{GameInstance.LoaderDisplayName(loader)} no tiene versiones para Minecraft {minecraft}.";
        }
        catch (Exception ex) // las listas de Forge y NeoForge (CmlLib) pueden fallar de varias formas
        {
            if (cts.IsCancellationRequested)
                return;
            _logger.LogWarning(ex, "No se pudieron consultar las versiones de {Loader}", loader);
            LoaderStatus = $"No se pudo consultar {GameInstance.LoaderDisplayName(loader)}. Comprueba la conexión.";
        }
    }

    partial void OnIsNewModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsExistingMode));
        OnPropertyChanged(nameof(ConfirmText));
    }

    // ── Confirmar ───────────────────────────────────────────────────────

    [RelayCommand]
    private void Confirm()
    {
        Error = null;
        if (!IsNewMode)
        {
            if (Instances.FirstOrDefault(i => i.IsSelected) is not { } choice)
            {
                Error = "Elige una instancia.";
                return;
            }

            Close(choice.Instance);
            return;
        }

        string name;
        try
        {
            name = InstanceStore.ValidateName(NewName);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return;
        }

        if (SelectedVersion is not { } minecraft || SelectedLoader is not { } loader)
        {
            Error = "Elige la versión de Minecraft y el cargador.";
            return;
        }

        if (loader != ModLoader.Vanilla && ResolvedLoaderVersion is null)
        {
            Error = LoaderStatus.Length > 0 ? LoaderStatus : "Espera a que se cargue la versión del cargador.";
            return;
        }

        try
        {
            var created = _store.Create(new GameInstance
            {
                Name = name,
                MinecraftVersion = minecraft,
                Loader = loader,
                LoaderChannel = loader == ModLoader.Vanilla ? LoaderChannel.Stable : UseLatestLoader ? LoaderChannel.Latest : LoaderChannel.Stable,
                LoaderVersion = loader == ModLoader.Vanilla ? null : ResolvedLoaderVersion,
            });
            _logger.LogInformation("Instancia {Id} creada para instalar contenido de Modrinth", created.Id);
            InstanceCreated?.Invoke(this, created);
            Close(created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error = $"No se pudo crear la instancia: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => Close(null);

    private void Close(GameInstance? result)
    {
        _load?.Cancel();
        _loaderLoad?.Cancel();
        IsOpen = false;
        _result?.TrySetResult(result);
    }

    [GeneratedRegex(@"^\d+\.\d+(\.\d+)?$")]
    private static partial Regex ReleaseVersion();
}
