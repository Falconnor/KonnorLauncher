using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Modrinth;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Opción de un filtro desplegable (Value null = sin filtro).</summary>
public sealed record FilterChoice(string? Value, string Label);

/// <summary>Versión de la lista de la página de un proyecto.</summary>
public sealed partial class VersionRow : ObservableObject
{
    public VersionRow(ProjectVersion version, Func<VersionRow, Task> install)
    {
        Version = version;
        InstallCommand = new AsyncRelayCommand(() => install(this), () => !IsInstalling);
    }

    public ProjectVersion Version { get; }
    public string Name => Version.Name.Length > 0 ? Version.Name : Version.VersionNumber;
    public string Number => Version.VersionNumber;

    public string TypeLabel => Version.VersionType switch
    {
        "beta" => "Beta",
        "alpha" => "Alfa",
        _ => "Estable",
    };

    public string TypeKind => Version.VersionType is "beta" or "alpha" ? Version.VersionType : "release";
    public string GameVersionsText => ProjectPageViewModel.CompactVersions(Version.GameVersions, 4);
    public string LoadersText => string.Join(", ", Version.Loaders.Select(BrowseText.Loader));
    public string DateText => Version.DatePublished.ToLocalTime().ToString("d MMM yyyy", CultureInfo.GetCultureInfo("es-ES"));
    public string DownloadsText => BrowseText.Compact(Version.Downloads);

    public IAsyncRelayCommand InstallCommand { get; }

    [ObservableProperty] private bool _isInstalling;
    partial void OnIsInstallingChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();
}

/// <summary>Imagen de la galería.</summary>
public sealed partial class GalleryItem(GalleryImage image) : ObservableObject
{
    public GalleryImage Image { get; } = image;
    public string? Title => Image.Title;
    public bool HasTitle => !string.IsNullOrWhiteSpace(Image.Title);
    [ObservableProperty] private ImageSource? _source;
}

/// <summary>Enlace de la columna de información (problemas, código, wiki, Discord, donaciones).</summary>
public sealed record ProjectLink(string Label, string Icon, string Url);

/// <summary>
/// Página de presentación de un proyecto de Modrinth, como en su aplicación: cabecera con el botón de
/// instalar, descripción completa, versiones y galería; a la derecha, compatibilidad, enlaces y detalles.
/// </summary>
public sealed partial class ProjectPageViewModel : ObservableObject
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Versiones que se muestran (las más recientes): algunos proyectos tienen miles.</summary>
    private const int MaxVersions = 150;

    private readonly ModrinthClient _client;
    private readonly IconLoader _icons;
    private readonly Func<VersionRow, Task> _installVersion;

    public ProjectPageViewModel(ProjectCard card, ModrinthClient client, IconLoader icons, Func<VersionRow, Task> installVersion)
    {
        Card = card;
        _client = client;
        _icons = icons;
        _installVersion = installVersion;
    }

    /// <summary>La tarjeta de la que se abrió: comparte con ella el botón de instalar y su estado.</summary>
    public ProjectCard Card { get; }

    public ObservableCollection<VersionRow> Versions { get; } = [];
    public ObservableCollection<GalleryItem> Gallery { get; } = [];
    public ObservableCollection<ProjectLink> Links { get; } = [];
    public ObservableCollection<CardTag> Loaders { get; } = [];
    public ObservableCollection<string> GameVersionGroups { get; } = [];

    [ObservableProperty] private ProjectDetails? _details;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _descriptionHtml;
    [ObservableProperty] private string _section = "description";
    [ObservableProperty] private bool _isVersionsLoading;

    // Filtros de la pestaña Versiones (null = todas)
    public ObservableCollection<FilterChoice> VersionChoices { get; } = [];
    public ObservableCollection<FilterChoice> LoaderChoices { get; } = [];
    [ObservableProperty] private string? _versionFilter;
    [ObservableProperty] private string? _loaderFilter;
    [ObservableProperty] private bool _isVersionFilterOpen;
    [ObservableProperty] private bool _isLoaderFilterOpen;

    public string VersionFilterLabel => VersionFilter ?? "Todas";
    public string LoaderFilterLabel => LoaderFilter is null ? "Todos" : BrowseText.Loader(LoaderFilter);
    public string VersionsEmptyText => _allVersions.Count == 0
        ? "Este proyecto no tiene versiones publicadas."
        : "Ninguna versión coincide con los filtros.";

    partial void OnVersionFilterChanged(string? value)
    {
        OnPropertyChanged(nameof(VersionFilterLabel));
        RebuildVersions();
    }

    partial void OnLoaderFilterChanged(string? value)
    {
        OnPropertyChanged(nameof(LoaderFilterLabel));
        RebuildVersions();
    }

    [RelayCommand]
    private void ChooseVersionFilter(string? value)
    {
        IsVersionFilterOpen = false;
        VersionFilter = value;
    }

    [RelayCommand]
    private void ChooseLoaderFilter(string? value)
    {
        IsLoaderFilterOpen = false;
        LoaderFilter = value;
    }

    public bool HasGallery => Gallery.Count > 0;
    public bool HasLinks => Links.Count > 0;
    // Las pestañas de sección se enlazan en los dos sentidos: marcar una cambia la sección.
    public bool IsDescription { get => Section == "description"; set { if (value) Section = "description"; } }
    public bool IsVersions { get => Section == "versions"; set { if (value) Section = "versions"; } }
    public bool IsGallery { get => Section == "gallery"; set { if (value) Section = "gallery"; } }
    public string VersionsTabText => Versions.Count > 0 ? $"Versiones ({Versions.Count})" : "Versiones";
    public string GalleryTabText => $"Galería ({Gallery.Count})";

    public string EnvironmentText => BrowseText.EnvironmentLabel(Details?.ClientSide, Details?.ServerSide) ?? "—";
    public string LicenseText => Details?.License is { } license
        ? (string.IsNullOrWhiteSpace(license.Name) ? license.Id : license.Name!).Replace("LicenseRef-", string.Empty)
        : "—";
    public string PublishedText => Details is null ? "—" : Details.Published.ToLocalTime().ToString("d MMM yyyy", Es);
    public string UpdatedText => Details is null ? "—" : BrowseText.Ago(Details.Updated);
    public string DownloadsText => BrowseText.Compact(Details?.Downloads ?? Card.Hit.Downloads);
    public string FollowersText => BrowseText.Compact(Details?.Followers ?? Card.Hit.Follows);

    // ── Carga ───────────────────────────────────────────────────────────────

    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var details = await _client.GetProjectAsync(Card.Hit.ProjectId, ct);
            Details = details;
            DescriptionHtml = MarkdownPage.Render(details.Body, details.Title);
            BuildInfo(details);
            foreach (var image in details.Gallery.OrderByDescending(g => g.Featured).ThenBy(g => g.Ordering))
            {
                var item = new GalleryItem(image);
                Gallery.Add(item);
                _ = LoadImageAsync(item);
            }

            OnPropertyChanged(nameof(HasGallery));
            OnPropertyChanged(nameof(GalleryTabText));
            IsLoading = false;
            await LoadVersionsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Error = ex is ModrinthException ? ex.Message : "No se pudo cargar la página del proyecto.";
            IsLoading = false;
        }
    }

    private async Task LoadImageAsync(GalleryItem item) => item.Source = await _icons.LoadAsync(item.Image.Url, decodeWidth: 640);

    private List<ProjectVersion> _allVersions = [];

    private async Task LoadVersionsAsync(CancellationToken ct)
    {
        IsVersionsLoading = true;
        try
        {
            _allVersions = (await _client.GetVersionsAsync(Card.Hit.ProjectId, ct: ct)).ToList();
            BuildVersionChoices();
            RebuildVersions();
        }
        catch (ModrinthException)
        {
            // La descripción ya está; sin versiones, la pestaña queda vacía.
        }
        finally
        {
            IsVersionsLoading = false;
        }
    }

    /// <summary>
    /// Las versiones que pasan los filtros (al instalar una, el diálogo dice en qué instancias sirve).
    /// </summary>
    private void RebuildVersions()
    {
        Versions.Clear();
        var matching = _allVersions.Where(v => (VersionFilter is null || v.GameVersions.Contains(VersionFilter))
                                               && (LoaderFilter is null || v.Loaders.Contains(LoaderFilter)));
        foreach (var version in matching.Take(MaxVersions))
            Versions.Add(new VersionRow(version, _installVersion));
        OnPropertyChanged(nameof(VersionsTabText));
        OnPropertyChanged(nameof(VersionsEmptyText));
    }

    private static readonly string[] MainLoaderOrder = ["forge", "fabric", "neoforge", "quilt"];

    /// <summary>
    /// Opciones de los filtros: las versiones de Minecraft estables que aparecen en alguna versión (de la
    /// más reciente a la más antigua) y sus cargadores (los principales primero).
    /// </summary>
    private void BuildVersionChoices()
    {
        VersionChoices.Clear();
        VersionChoices.Add(new FilterChoice(null, "Todas"));
        var releases = _allVersions.SelectMany(v => v.GameVersions).Distinct()
            .Select(text => (Text: text, Parsed: Version.TryParse(text, out var parsed) ? parsed : null))
            .Where(v => v.Parsed is not null)
            .OrderByDescending(v => v.Parsed);
        foreach (var (text, _) in releases)
            VersionChoices.Add(new FilterChoice(text, text));

        LoaderChoices.Clear();
        LoaderChoices.Add(new FilterChoice(null, "Todos"));
        var loaders = _allVersions.SelectMany(v => v.Loaders).Distinct().Where(BrowseText.IsPlayerLoader)
            .OrderBy(l => Array.IndexOf(MainLoaderOrder, l) is var i and >= 0 ? i : MainLoaderOrder.Length)
            .ThenBy(l => l, StringComparer.Ordinal);
        foreach (var loader in loaders)
            LoaderChoices.Add(new FilterChoice(loader, BrowseText.Loader(loader)));
    }

    private void BuildInfo(ProjectDetails details)
    {
        Loaders.Clear();
        foreach (var loader in details.Loaders.Where(BrowseText.IsPlayerLoader))
            Loaders.Add(new CardTag(BrowseText.Loader(loader), loader));

        GameVersionGroups.Clear();
        foreach (var group in GroupVersions(details.GameVersions))
            GameVersionGroups.Add(group);

        Links.Clear();
        void Add(string label, string icon, string? url)
        {
            if (ContentInstaller.IsAllowedUrl(url, out _))
                Links.Add(new ProjectLink(label, icon, url!));
        }

        Add("Reportar un problema", "", details.IssuesUrl);
        Add("Código fuente", "", details.SourceUrl);
        Add("Wiki", "", details.WikiUrl);
        Add("Discord", "", details.DiscordUrl);
        foreach (var donation in details.DonationUrls)
            Add($"Donar ({BrowseText.Category(donation.Platform)})", "", donation.Url);
        Add("Ver en Modrinth", "", $"https://modrinth.com/{Uri.EscapeDataString(details.ProjectType)}/{Uri.EscapeDataString(details.Slug)}");

        foreach (var name in new[] { nameof(HasLinks), nameof(EnvironmentText), nameof(LicenseText), nameof(PublishedText),
                     nameof(UpdatedText), nameof(DownloadsText), nameof(FollowersText) })
            OnPropertyChanged(name);
    }

    // ── Secciones ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void ShowSection(string section) => Section = section;

    partial void OnSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsDescription));
        OnPropertyChanged(nameof(IsVersions));
        OnPropertyChanged(nameof(IsGallery));
    }

    // ── Versiones de Minecraft agrupadas ────────────────────────────────────

    /// <summary>Solo versiones normales (no snapshots), de la más reciente a la más antigua: "1.21.x", "1.20.1"...</summary>
    public static List<string> GroupVersions(IEnumerable<string> versions)
    {
        var releases = versions.Where(v => ReleaseVersion().IsMatch(v)).Distinct().ToList();
        return releases
            .GroupBy(v => string.Join('.', v.Split('.').Take(2)))
            .OrderByDescending(g => ParseVersion(g.Key))
            .Select(g => g.Count() > 1 ? g.Key + ".x" : g.First())
            .ToList();
    }

    /// <summary>"1.21.1, 1.21, 1.20.6 y 5 más".</summary>
    public static string CompactVersions(IReadOnlyList<string> versions, int max)
    {
        var releases = versions.Where(v => ReleaseVersion().IsMatch(v)).OrderByDescending(ParseVersion).ToList();
        var shown = releases.Count > 0 ? releases : versions.ToList();
        return shown.Count <= max ? string.Join(", ", shown) : $"{string.Join(", ", shown.Take(max))} y {shown.Count - max} más";
    }

    private static Version ParseVersion(string text) =>
        System.Version.TryParse(text.Count(c => c == '.') == 0 ? text + ".0" : text, out var parsed) ? parsed : new Version(0, 0);

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+\.\d+(\.\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex ReleaseVersion();
}
