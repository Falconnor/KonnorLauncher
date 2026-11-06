using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Modrinth;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>
/// Página "Explorar contenido": busca en Modrinth por tipo (modpacks, mods, paquetes de recursos, data
/// packs, shaders y servidores), con filtros, orden y paginación, e instala en la instancia elegida.
/// Los modpacks y los servidores se instalan como una instancia nueva.
/// </summary>
public sealed partial class BrowseViewModel : ObservableObject
{
    /// <summary>Espera tras el último cambio antes de buscar (así no se busca a cada tecla).</summary>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(350);

    private readonly ModrinthClient _client;
    private readonly ContentInstaller _installer;
    private readonly InstanceStore _store;
    private readonly IconLoader _icons;
    private readonly ILogger<BrowseViewModel> _logger;

    private CancellationTokenSource? _search;
    private IReadOnlyList<CategoryTag> _categoryTags = [];
    private IReadOnlyList<LoaderTag> _loaderTags = [];
    private IReadOnlyList<GameVersionTag> _versionTags = [];
    private bool _tagsLoaded;
    private bool _suspend; // mientras se reconstruyen los filtros no se lanza una búsqueda por cada cambio
    private TaskCompletionSource<string?>? _worldChoice;

    public BrowseViewModel(ModrinthClient client, ContentInstaller installer, InstanceStore store, IconLoader icons,
        InstallDialogViewModel installDialog, ILogger<BrowseViewModel> logger)
    {
        _client = client;
        _installer = installer;
        _store = store;
        _icons = icons;
        _logger = logger;
        InstallDialog = installDialog;

        Tabs =
        [
            new BrowseTab(ContentType.Modpack, "Modpacks", "", OnTabSelected),
            new BrowseTab(ContentType.Mod, "Mods", "", OnTabSelected),
            new BrowseTab(ContentType.ResourcePack, "Paquetes de recursos", "", OnTabSelected),
            new BrowseTab(ContentType.DataPack, "Data packs", "", OnTabSelected),
            new BrowseTab(ContentType.Shader, "Shaders", "", OnTabSelected),
            new BrowseTab(ContentType.Server, "Servidores", "", OnTabSelected),
        ];
        GoToPageCommand = new RelayCommand<int>(page => { Page = page; ScheduleSearch(resetPage: false, immediate: true); });
    }

    /// <summary>"¿Dónde instalar?" (lo muestra la ventana como diálogo).</summary>
    public InstallDialogViewModel InstallDialog { get; }

    /// <summary>Se creó una instancia con un modpack (la ventana la añade a la lista).</summary>
    public event EventHandler<GameInstance>? InstanceCreated;

    /// <summary>Se instaló contenido en una instancia (la ventana actualiza sus contadores).</summary>
    public event EventHandler<string>? InstanceChanged;

    /// <summary>Llegaron resultados nuevos (la vista vuelve arriba).</summary>
    public event EventHandler? ResultsReplaced;

    public ObservableCollection<BrowseTab> Tabs { get; }
    public ObservableCollection<ProjectCard> Results { get; } = [];
    public ObservableCollection<PageItem> Pages { get; } = [];
    public ObservableCollection<FilterChip> Chips { get; } = [];
    public ObservableCollection<CategoryFilter> Categories { get; } = [];
    public ObservableCollection<FilterOption> Features { get; } = [];
    public ObservableCollection<FilterOption> VisibleFeatures { get; } = [];
    public ObservableCollection<FilterOption> Environments { get; } = [];
    public ObservableCollection<FilterOption> GameVersions { get; } = [];
    public ObservableCollection<FilterOption> Loaders { get; } = [];

    /// <summary>Forge, Fabric, NeoForge y Quilt (los que haya en la pestaña), siempre a la vista.</summary>
    public ObservableCollection<FilterOption> MainLoaders { get; } = [];

    /// <summary>El resto de cargadores, al desplegar "Más cargadores".</summary>
    public ObservableCollection<FilterOption> OtherLoaders { get; } = [];

    [ObservableProperty] private bool _showOtherLoaders;

    public bool HasOtherLoaders => OtherLoaders.Count > 0;

    private static readonly string[] MainLoaderOrder = ["forge", "fabric", "neoforge", "quilt"];
    public ObservableCollection<string> Worlds { get; } = [];
    public IRelayCommand<int> GoToPageCommand { get; }

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new(SearchSort.Relevance, "Relevancia"),
        new(SearchSort.Downloads, "Descargas"),
        new(SearchSort.Follows, "Seguidores"),
        new(SearchSort.Newest, "Más recientes"),
        new(SearchSort.Updated, "Actualizados"),
    ];

    public IReadOnlyList<int> PageSizes { get; } = [5, 10, 20, 50, 100];

    [ObservableProperty] private ContentType _activeType = ContentType.Mod;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private SearchSort _sort = SearchSort.Relevance;
    [ObservableProperty] private int _pageSize = 20;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalHits;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _isNoticeError;
    [ObservableProperty] private bool _isSortOpen;
    [ObservableProperty] private bool _isPageSizeOpen;
    [ObservableProperty] private bool _showSnapshots;
    [ObservableProperty] private bool _openSourceOnly;
    [ObservableProperty] private string _featureFilter = string.Empty;
    [ObservableProperty] private bool _isWorldPickerOpen;
    [ObservableProperty] private string _worldPickerTitle = string.Empty;

    // Grupos del panel de filtros: plegados o no.
    [ObservableProperty] private bool _isCategoryExpanded = true;
    [ObservableProperty] private bool _isFeatureExpanded = true;
    [ObservableProperty] private bool _isEnvironmentExpanded = true;
    [ObservableProperty] private bool _isVersionExpanded = true;
    [ObservableProperty] private bool _isLoaderExpanded = true;
    [ObservableProperty] private bool _isLicenseExpanded;

    public string SortLabel => SortOptions.First(o => o.Value == Sort).Label;
    public bool CreatesInstance => ActiveType.CreatesInstance();
    public bool IsEmpty => !IsLoading && Error is null && Results.Count == 0;
    public bool HasPages => TotalPages > 1;
    public int TotalPages => TotalHits == 0 ? 0 : (int)Math.Ceiling(TotalHits / (double)PageSize);
    public string TotalText => TotalHits == 1 ? "1 resultado" : $"{BrowseText.Compact(TotalHits)} resultados";
    public string SearchPlaceholder => $"Buscar {Tabs.First(t => t.Type == ActiveType).Label.ToLowerInvariant()}…";

    public bool ShowFeatures => Features.Count > 0;
    public bool ShowEnvironment => ActiveType is ContentType.Mod or ContentType.Modpack;
    public bool ShowLoaders => Loaders.Count > 1;
    public int ActiveFilterCount => Chips.Count;
    public string CategoryActiveText => ActiveText(Categories.Count(c => c.State != CategoryState.None));
    public string FeatureActiveText => ActiveText(Features.Count(f => f.IsActive));
    public string EnvironmentActiveText => ActiveText(Environments.Count(f => f.IsActive));
    public string VersionActiveText => ActiveText(GameVersions.Count(f => f.IsActive));
    public string LoaderActiveText => ActiveText(Loaders.Count(f => f.IsActive));
    public string LicenseActiveText => ActiveText(OpenSourceOnly ? 1 : 0);

    private static string ActiveText(int count) => count == 0 ? string.Empty : count == 1 ? "1 activo" : $"{count} activos";

    // ══════════════════════════════════════════════════════════════════════
    // ABRIR
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Abre la página (la primera vez carga las listas de categorías, cargadores y versiones).</summary>
    public async Task OpenAsync()
    {
        Notice = null;
        if (!_tagsLoaded)
        {
            IsLoading = true;
            try
            {
                var categories = _client.GetCategoriesAsync();
                var loaders = _client.GetLoadersAsync();
                var versions = _client.GetGameVersionsAsync();
                await Task.WhenAll(categories, loaders, versions);
                (_categoryTags, _loaderTags, _versionTags) = (categories.Result, loaders.Result, versions.Result);
                _tagsLoaded = true;
            }
            catch (Exception ex)
            {
                // Sin las listas de categorías y versiones no se pueden mostrar los filtros.
                _logger.LogWarning(ex, "No se pudieron cargar las etiquetas de Modrinth");
                IsLoading = false;
                Error = ex is ModrinthException ? ex.Message : "No se pudo conectar con Modrinth.";
                return;
            }
        }

        SelectType(ActiveType, keepText: true);
    }

    /// <summary>Abre la página en una pestaña concreta (los accesos de la barra lateral).</summary>
    public Task OpenAsync(ContentType type)
    {
        if (!_tagsLoaded)
        {
            ActiveType = type; // se selecciona al terminar de cargar las listas
            return OpenAsync();
        }

        Notice = null;
        SelectType(type, keepText: type == ActiveType);
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════════
    // PESTAÑAS Y FILTROS
    // ══════════════════════════════════════════════════════════════════════

    private bool _selectingTab;

    /// <summary>Pestaña marcada por el jugador.</summary>
    private void OnTabSelected(BrowseTab tab)
    {
        if (!_selectingTab && _tagsLoaded)
            SelectType(tab.Type, keepText: false);
    }

    private void SelectType(ContentType type, bool keepText)
    {
        CloseProject();
        _suspend = true;
        ActiveType = type;
        _selectingTab = true;
        foreach (var tab in Tabs)
            tab.IsActive = tab.Type == type;
        _selectingTab = false;
        if (!keepText)
            SearchText = string.Empty;
        BuildFilters();
        _suspend = false;
        OnPropertyChanged(nameof(CreatesInstance));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(ShowEnvironment));
        ScheduleSearch(resetPage: true, immediate: true);
    }

    private void BuildFilters()
    {
        var projectType = ActiveType.ProjectType();
        Categories.Clear();
        foreach (var tag in _categoryTags.Where(c => c.ProjectType == projectType && IsMainHeader(c.Header)).OrderBy(c => BrowseText.Category(c.Name)))
            Categories.Add(new CategoryFilter(tag.Name, BrowseText.Category(tag.Name), FiltersChanged));

        Features.Clear();
        foreach (var tag in _categoryTags.Where(c => c.ProjectType == projectType && !IsMainHeader(c.Header)))
            Features.Add(new FilterOption(tag.Name, BrowseText.Category(tag.Name), FiltersChanged));
        FeatureFilter = string.Empty;
        RefreshVisibleFeatures();

        Environments.Clear();
        Environments.Add(new FilterOption("client", "Cliente", FiltersChanged));
        Environments.Add(new FilterOption("server", "Servidor", FiltersChanged));

        BuildGameVersions(selected: []);

        Loaders.Clear();
        var loaderType = ActiveType == ContentType.Server ? "modpack" : projectType;
        foreach (var tag in _loaderTags.Where(l => l.SupportedProjectTypes.Contains(loaderType) && IsPlayerLoader(l.Name)))
            Loaders.Add(new FilterOption(tag.Name, BrowseText.Loader(tag.Name), FiltersChanged));
        SplitLoaders();

        OpenSourceOnly = false;
        OnPropertyChanged(nameof(ShowFeatures));
        OnPropertyChanged(nameof(ShowLoaders));
    }

    /// <summary>
    /// Separa los cargadores principales del resto. Si la pestaña no tiene ninguno de los principales
    /// (shaders: Iris, OptiFine...), todos se ven sin desplegar nada.
    /// </summary>
    private void SplitLoaders()
    {
        MainLoaders.Clear();
        OtherLoaders.Clear();
        var main = MainLoaderOrder.Select(name => Loaders.FirstOrDefault(l => l.Value == name)).OfType<FilterOption>().ToList();
        if (main.Count == 0)
            main = [.. Loaders];
        foreach (var loader in main)
            MainLoaders.Add(loader);
        foreach (var loader in Loaders.Except(main))
            OtherLoaders.Add(loader);
        ShowOtherLoaders = false;
        OnPropertyChanged(nameof(HasOtherLoaders));
    }

    /// <summary>Las de servidores y "categories" son las categorías; el resto, "contenido incluido".</summary>
    private static bool IsMainHeader(string header) => header is "categories" || header.StartsWith("minecraft_server", StringComparison.Ordinal);

    /// <summary>Cargadores del filtro: los de un jugador; los data packs ya tienen su pestaña.</summary>
    private static bool IsPlayerLoader(string name) => BrowseText.IsPlayerLoader(name) && name != "datapack";

    private void BuildGameVersions(IReadOnlyCollection<string> selected)
    {
        GameVersions.Clear();
        foreach (var tag in _versionTags.Where(v => ShowSnapshots || v.VersionType == "release"))
            GameVersions.Add(new FilterOption(tag.Version, tag.Version, FiltersChanged) { IsActive = selected.Contains(tag.Version) });
    }

    partial void OnShowSnapshotsChanged(bool value)
    {
        var selected = GameVersions.Where(v => v.IsActive).Select(v => v.Value).ToList();
        var suspended = _suspend;
        _suspend = true;
        BuildGameVersions(selected);
        _suspend = suspended;
    }

    partial void OnFeatureFilterChanged(string value) => RefreshVisibleFeatures();

    private void RefreshVisibleFeatures()
    {
        VisibleFeatures.Clear();
        foreach (var feature in Features.Where(f => FeatureFilter.Length == 0 || f.Label.Contains(FeatureFilter.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            VisibleFeatures.Add(feature);
    }

    partial void OnOpenSourceOnlyChanged(bool value) => FiltersChanged();

    private void FiltersChanged()
    {
        if (!_suspend)
            ScheduleSearch(resetPage: true, immediate: false);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suspend = true;
        foreach (var category in Categories)
            category.State = CategoryState.None;
        foreach (var option in Features.Concat(Environments).Concat(GameVersions).Concat(Loaders))
            option.IsActive = false;
        OpenSourceOnly = false;
        _suspend = false;
        ScheduleSearch(resetPage: true, immediate: true);
    }

    private void RebuildChips()
    {
        Chips.Clear();
        foreach (var category in Categories.Where(c => c.State != CategoryState.None))
            Chips.Add(new FilterChip(category.IsExcluded ? $"Sin {category.Label.ToLowerInvariant()}" : category.Label, category.IsExcluded,
                () => category.State = CategoryState.None));
        foreach (var option in Features.Concat(Environments).Concat(GameVersions).Concat(Loaders).Where(o => o.IsActive))
            Chips.Add(new FilterChip(option.Label, false, () => option.IsActive = false));
        if (OpenSourceOnly)
            Chips.Add(new FilterChip("Código abierto", false, () => OpenSourceOnly = false));

        foreach (var name in new[] { nameof(ActiveFilterCount), nameof(CategoryActiveText), nameof(FeatureActiveText), nameof(EnvironmentActiveText),
                     nameof(VersionActiveText), nameof(LoaderActiveText), nameof(LicenseActiveText) })
            OnPropertyChanged(name);
    }

    // ── Orden, cantidad por página y paginación ──────────────────────────

    [RelayCommand]
    private void ChooseSort(SearchSort sort)
    {
        IsSortOpen = false;
        Sort = sort;
    }

    partial void OnSortChanged(SearchSort value)
    {
        OnPropertyChanged(nameof(SortLabel));
        ScheduleSearch(resetPage: true, immediate: true);
    }

    [RelayCommand]
    private void ChoosePageSize(int size)
    {
        IsPageSizeOpen = false;
        PageSize = size;
    }

    partial void OnPageSizeChanged(int value) => ScheduleSearch(resetPage: true, immediate: true);

    partial void OnSearchTextChanged(string value) => ScheduleSearch(resetPage: true, immediate: false);

    [RelayCommand]
    private void PreviousPage()
    {
        if (Page <= 1)
            return;
        Page--;
        ScheduleSearch(resetPage: false, immediate: true);
    }

    [RelayCommand]
    private void NextPage()
    {
        if (Page >= TotalPages)
            return;
        Page++;
        ScheduleSearch(resetPage: false, immediate: true);
    }

    /// <summary>1 … 4 5 6 … 20: la primera, la última y dos a cada lado de la actual.</summary>
    private void RebuildPages()
    {
        Pages.Clear();
        var total = TotalPages;
        if (total <= 1)
            return;
        var shown = new SortedSet<int> { 1, total };
        for (var p = Page - 1; p <= Page + 1; p++)
            if (p >= 1 && p <= total)
                shown.Add(p);
        var previous = 0;
        foreach (var p in shown)
        {
            if (p - previous > 1)
                Pages.Add(new PageItem(null, false, GoToPageCommand));
            Pages.Add(new PageItem(p, p == Page, GoToPageCommand));
            previous = p;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // BÚSQUEDA
    // ══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (!_tagsLoaded)
            await OpenAsync(); // falló al abrir: se vuelve a intentar entero
        else
            ScheduleSearch(resetPage: false, immediate: true);
    }

    private void ScheduleSearch(bool resetPage, bool immediate)
    {
        if (_suspend || !_tagsLoaded)
            return;
        if (resetPage)
            Page = 1;
        RebuildChips();
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        _ = SearchAsync(immediate ? TimeSpan.Zero : SearchDelay, cts.Token);
    }

    private SearchQuery BuildQuery() => new()
    {
        Type = ActiveType,
        Text = SearchText,
        Sort = Sort,
        Limit = PageSize,
        Page = Page,
        IncludedCategories = Categories.Where(c => c.IsIncluded).Select(c => c.Value)
            .Concat(Features.Where(f => f.IsActive).Select(f => f.Value)).ToList(),
        ExcludedCategories = Categories.Where(c => c.IsExcluded).Select(c => c.Value).ToList(),
        GameVersions = GameVersions.Where(v => v.IsActive).Select(v => v.Value).ToList(),
        Loaders = Loaders.Where(l => l.IsActive).Select(l => l.Value).ToList(),
        Environments = ShowEnvironment
            ? Environments.Where(e => e.IsActive).Select(e => e.Value == "client" ? ProjectEnvironment.Client : ProjectEnvironment.Server).ToList()
            : [],
        OpenSourceOnly = OpenSourceOnly,
    };

    private async Task SearchAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
            IsLoading = true;
            Error = null;
            OnPropertyChanged(nameof(IsEmpty));
            var page = await _client.SearchAsync(BuildQuery(), ct);
            ct.ThrowIfCancellationRequested();

            Results.Clear();
            foreach (var hit in page.Hits)
            {
                var card = new ProjectCard(hit, ActiveType, InstallAsync);
                Results.Add(card);
                _ = LoadIconAsync(card);
            }

            TotalHits = page.TotalHits;
            RebuildPages();
            ResultsReplaced?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // la sustituyó una búsqueda más reciente
        }
        catch (Exception ex)
        {
            if (ex is not ModrinthException)
                _logger.LogWarning(ex, "Error inesperado buscando en Modrinth");
            Results.Clear();
            TotalHits = 0;
            RebuildPages();
            Error = ex is ModrinthException ? ex.Message : "No se pudo buscar en Modrinth.";
        }

        IsLoading = false;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(TotalText));
    }

    private async Task LoadIconAsync(ProjectCard card) => card.Icon = await _icons.LoadAsync(card.Hit.IconUrl);

    // ══════════════════════════════════════════════════════════════════════
    // INSTALAR
    // ══════════════════════════════════════════════════════════════════════

    private Task InstallAsync(ProjectCard card) => InstallAsync(card, versionId: null);

    /// <summary>
    /// Instala el proyecto de la tarjeta; con <paramref name="versionId"/>, esa versión concreta. Los
    /// modpacks crean su instancia; el resto pregunta antes dónde instalar (o crea una instancia nueva).
    /// </summary>
    private async Task InstallAsync(ProjectCard card, string? versionId)
    {
        Notice = null;
        GameInstance? target = null;
        if (!card.Type.CreatesInstance())
        {
            target = await InstallDialog.ShowAsync(card, versionId);
            if (target is null)
                return; // cancelado
        }

        var progress = new Progress<InstallProgress>(p =>
        {
            card.IsProgressKnown = p.Percent is not null;
            card.Progress = p.Percent ?? 0;
        });
        card.State = CardState.Installing;
        try
        {
            if (card.Type.CreatesInstance())
            {
                // Si hay filtros de versión o de cargador, se instala la versión del modpack que los cumple.
                var loaders = Loaders.Where(l => l.IsActive).Select(l => l.Value).ToList();
                var versions = GameVersions.Where(v => v.IsActive).Select(v => v.Value).ToList();
                var instance = await Task.Run(() => _installer.InstallModpackAsync(card.Hit.ProjectId, card.Hit.Title, progress,
                    loaders: loaders, gameVersions: versions, versionId: versionId));
                card.State = CardState.Installed;
                ShowNotice($"Instancia «{instance.Name}» creada con {card.Title}. Al pulsar JUGAR se instala {instance.Summary}.");
                InstanceCreated?.Invoke(this, instance);
                return;
            }

            string? world = null;
            if (card.Type == ContentType.DataPack)
            {
                world = await ChooseWorldAsync(target!, card.Title);
                if (world is null)
                {
                    card.State = CardState.Available;
                    return;
                }
            }

            var result = await Task.Run(() => _installer.InstallAsync(target!, card.Hit.ProjectId, card.Type, world, progress,
                versionId: versionId));
            card.State = CardState.Installed;
            var extra = result.Dependencies.Count switch
            {
                0 => string.Empty,
                1 => " y 1 dependencia",
                var n => $" y {n} dependencias",
            };
            var missing = result.MissingDependencies.Count > 0
                ? $" No se encontró versión compatible de {result.MissingDependencies.Count} dependencia(s): puede que no funcione."
                : string.Empty;
            ShowNotice($"{card.Title}{extra} instalado en «{target!.Name}»{(world is null ? string.Empty : $", mundo «{world}»")}.{missing}",
                error: missing.Length > 0);
            InstanceChanged?.Invoke(this, target!.Id);
        }
        catch (Exception ex) when (ex is ModrinthException or IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "No se pudo instalar {Project}", card.Hit.ProjectId);
            card.State = CardState.Available;
            ShowNotice($"No se pudo instalar {card.Title}: {ex.Message}", error: true);
        }
    }

    private void ShowNotice(string text, bool error = false)
    {
        IsNoticeError = error;
        Notice = text;
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    /// <summary>Los data packs van en un mundo: si hay varios, se pregunta en cuál.</summary>
    private async Task<string?> ChooseWorldAsync(GameInstance instance, string title)
    {
        var worlds = ContentInstaller.Worlds(_store.GetDirectory(instance.Id));
        if (worlds.Count == 0)
        {
            ShowNotice($"«{instance.Name}» no tiene mundos todavía. Entra al juego, crea un mundo y vuelve a instalar el data pack.", error: true);
            return null;
        }

        if (worlds.Count == 1)
            return worlds[0];
        Worlds.Clear();
        foreach (var world in worlds)
            Worlds.Add(world);
        WorldPickerTitle = $"¿En qué mundo instalar {title}?";
        _worldChoice = new TaskCompletionSource<string?>();
        IsWorldPickerOpen = true;
        var choice = await _worldChoice.Task;
        IsWorldPickerOpen = false;
        return choice;
    }

    [RelayCommand]
    private void ChooseWorld(string world) => _worldChoice?.TrySetResult(world);

    [RelayCommand]
    private void CancelWorldPicker() => _worldChoice?.TrySetResult(null);

    // ══════════════════════════════════════════════════════════════════════
    // PÁGINA DE UN PROYECTO
    // ══════════════════════════════════════════════════════════════════════

    private CancellationTokenSource? _projectLoad;

    /// <summary>Página del proyecto abierto (null = se ve la lista de resultados).</summary>
    [ObservableProperty] private ProjectPageViewModel? _project;

    public bool HasProject => Project is not null;

    partial void OnProjectChanged(ProjectPageViewModel? value) => OnPropertyChanged(nameof(HasProject));

    /// <summary>Clic en una tarjeta: su página de presentación, como en la aplicación de Modrinth.</summary>
    [RelayCommand]
    private void OpenProject(ProjectCard card)
    {
        _projectLoad?.Cancel();
        var cts = _projectLoad = new CancellationTokenSource();
        Notice = null;
        Project = new ProjectPageViewModel(card, _client, _icons, InstallVersionAsync);
        _ = Project.LoadAsync(cts.Token);
    }

    [RelayCommand]
    private void CloseProject()
    {
        _projectLoad?.Cancel();
        Project = null;
    }

    /// <summary>"Instalar" de una versión concreta en la página del proyecto.</summary>
    private async Task InstallVersionAsync(VersionRow row)
    {
        if (Project is not { } page)
            return;
        row.IsInstalling = true;
        try
        {
            await InstallAsync(page.Card, row.Version.Id);
        }
        finally
        {
            row.IsInstalling = false;
        }
    }

    [RelayCommand]
    private static void OpenLink(string url)
    {
        if (!ContentInstaller.IsAllowedUrl(url, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    // ── Menús desplegables ────────────────────────────────────────────────

    /// <summary>Esc: cierra lo que esté abierto en la página. Devuelve false si no había nada.</summary>
    public bool CloseTopmost()
    {
        if (IsWorldPickerOpen) { CancelWorldPicker(); return true; }
        if (IsSortOpen || IsPageSizeOpen) { IsSortOpen = IsPageSizeOpen = false; return true; }
        if (Project is not null) { CloseProject(); return true; }
        return false;
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnTotalHitsChanged(int value)
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(TotalText));
    }
}
