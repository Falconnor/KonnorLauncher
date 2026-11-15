using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using KonnorLauncher.Core.Modrinth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Stores;

/// <summary>
/// Modpacks de Feed The Beast (<c>api.feed-the-beast.com</c>, pública). La búsqueda solo devuelve números de modpack:
/// los detalles de cada uno se piden aparte (y se recuerdan), y los filtros y el orden se aplican aquí.
/// </summary>
public sealed class FtbClient : IContentStore
{
    public const string Prefix = "ftb:";
    public const string DefaultBaseUrl = "https://api.feed-the-beast.com/v1/modpacks/public";

    /// <summary>Cuántos modpacks se piden a la lista de populares o a la búsqueda.</summary>
    private const int ListSize = 100;

    private const int ParallelRequests = 8;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, Task<FtbPack>> _packs = new();

    public FtbClient(IHttpClientFactory httpFactory, ILogger<FtbClient>? logger = null, string baseUrl = DefaultBaseUrl)
    {
        _httpFactory = httpFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string BaseUrl { get; }
    public StoreKind Kind => StoreKind.Ftb;
    public string Name => "FTB";
    public bool Supports(ContentType type) => type == ContentType.Modpack;

    private static string Id(int pack) => Prefix + pack.ToString(CultureInfo.InvariantCulture);

    public static int ParseProjectId(string projectId) =>
        projectId.StartsWith(Prefix, StringComparison.Ordinal) && int.TryParse(projectId.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new ModrinthException("Modpack de FTB no válido.");

    public static (int Pack, int Version) ParseVersionId(string versionId)
    {
        var parts = versionId.StartsWith(Prefix, StringComparison.Ordinal) ? versionId[Prefix.Length..].Split(':') : [];
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pack)
                                 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? (pack, version)
            : throw new ModrinthException("Versión de FTB no válida.");
    }

    // ── Búsqueda ────────────────────────────────────────────────────────────

    public async Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (query.Type != ContentType.Modpack)
            return new SearchPage();

        var list = string.IsNullOrWhiteSpace(query.Text)
            ? await GetAsync<FtbList>($"/modpack/popular/installs/{ListSize}", ct).ConfigureAwait(false)
            : await GetAsync<FtbList>($"/modpack/search/{ListSize}?term={Uri.EscapeDataString(query.Text.Trim())}", ct).ConfigureAwait(false);
        var ids = list.Packs.Distinct().ToList();

        var limit = Math.Clamp(query.Limit, 1, SearchQuery.MaxLimit);
        var needsAll = query.GameVersions.Count > 0 || query.Loaders.Count > 0 || query.IncludedCategories.Count > 0
                       || query.ExcludedCategories.Count > 0 || query.Sort is SearchSort.Newest or SearchSort.Updated or SearchSort.Downloads;
        List<FtbPack> packs;
        int total;
        if (needsAll)
        {
            packs = (await LoadPacksAsync(ids, ct).ConfigureAwait(false))
                .Where(p => Matches(p, query))
                .ToList();
            packs = (query.Sort switch
            {
                SearchSort.Newest => packs.OrderByDescending(p => p.Released),
                SearchSort.Updated => packs.OrderByDescending(p => p.Updated),
                SearchSort.Downloads => packs.OrderByDescending(p => p.Installs),
                _ => packs.AsEnumerable(),
            }).ToList();
            total = packs.Count;
            packs = packs.Skip(query.Offset).Take(limit).ToList();
        }
        else
        {
            // Sin filtros: solo los de esta página (la lista ya viene ordenada por FTB).
            packs = (await LoadPacksAsync(ids.Skip(query.Offset).Take(limit), ct).ConfigureAwait(false)).ToList();
            total = ids.Count;
        }

        return new SearchPage { Hits = packs.Select(ToHit).ToList(), Offset = query.Offset, Limit = limit, TotalHits = total };
    }

    private static bool Matches(FtbPack pack, SearchQuery query)
    {
        var tags = pack.Tags.Select(t => t.Name).ToList();
        return (query.GameVersions.Count == 0 || GameVersionsOf(pack).Intersect(query.GameVersions).Any())
               && (query.Loaders.Count == 0 || LoadersOf(pack).Intersect(query.Loaders).Any())
               && query.IncludedCategories.All(tags.Contains)
               && !query.ExcludedCategories.Any(tags.Contains);
    }

    /// <summary>Los modpacks pedidos (los privados o sin versiones, fuera), en el mismo orden.</summary>
    private async Task<IReadOnlyList<FtbPack>> LoadPacksAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        var result = new FtbPack?[list.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, list.Count), new ParallelOptions { MaxDegreeOfParallelism = ParallelRequests, CancellationToken = ct },
            async (i, token) =>
            {
                try
                {
                    result[i] = await GetPackAsync(list[i], token).ConfigureAwait(false);
                }
                catch (ModrinthException ex)
                {
                    _logger.LogInformation("Modpack de FTB {Id} omitido: {Message}", list[i], ex.Message);
                }
            }).ConfigureAwait(false);
        return result.OfType<FtbPack>().Where(p => !p.Private && p.Versions.Count > 0).ToList();
    }

    private Task<FtbPack> GetPackAsync(int id, CancellationToken ct)
    {
        var task = _packs.GetOrAdd(id, key => GetAsync<FtbPack>($"/modpack/{key}", CancellationToken.None));
        if (task.IsFaulted || task.IsCanceled)
        {
            _packs.TryRemove(id, out _);
            task = _packs.GetOrAdd(id, key => GetAsync<FtbPack>($"/modpack/{key}", CancellationToken.None));
        }

        return task.WaitAsync(ct);
    }

    private static string? Art(FtbPack pack, string type) =>
        pack.Art.Where(a => a.Type == type).OrderBy(a => a.Order).FirstOrDefault()?.Url;

    private static IEnumerable<string> GameVersionsOf(FtbPack pack) =>
        pack.Versions.SelectMany(v => v.Targets).Where(t => t.Type == "game" && t.Name == "minecraft").Select(t => t.Version).Distinct();

    private static IEnumerable<string> LoadersOf(FtbPack pack) =>
        pack.Versions.SelectMany(v => v.Targets).Where(t => t.Type == "modloader").Select(t => t.Name.ToLowerInvariant()).Distinct();

    private static DateTimeOffset Time(long unix) => DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, unix));

    internal static SearchHit ToHit(FtbPack pack)
    {
        var tags = pack.Tags.Select(t => t.Name).Where(n => n.Length > 0 && !char.IsDigit(n[0])).ToList();
        return new SearchHit
        {
            ProjectId = Id(pack.Id),
            ProjectType = "modpack",
            Slug = pack.Slug,
            Title = pack.Name,
            Author = pack.Authors.FirstOrDefault()?.Name ?? "FTB",
            Description = pack.Synopsis,
            Categories = [.. tags, .. LoadersOf(pack)],
            DisplayCategories = tags,
            Downloads = pack.Installs,
            Follows = pack.Plays,
            IconUrl = Art(pack, "square"),
            DateModified = Time(pack.Updated),
        };
    }

    // ── Proyecto y versiones ────────────────────────────────────────────────

    public async Task<ProjectDetails> GetProjectAsync(string projectId, CancellationToken ct = default)
    {
        var pack = await GetPackAsync(ParseProjectId(projectId), ct).ConfigureAwait(false);
        string? Link(string name) => pack.Links.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Link;
        return new ProjectDetails
        {
            Id = Id(pack.Id),
            Slug = pack.Slug,
            ProjectType = "modpack",
            Title = pack.Name,
            Description = pack.Synopsis,
            Body = pack.Description,
            Categories = pack.Tags.Select(t => t.Name).Where(n => n.Length > 0 && !char.IsDigit(n[0])).ToList(),
            Loaders = LoadersOf(pack).ToList(),
            GameVersions = GameVersionsOf(pack).ToList(),
            Downloads = pack.Installs,
            Followers = pack.Plays,
            IconUrl = Art(pack, "square"),
            Published = Time(pack.Released),
            Updated = Time(pack.Updated),
            IssuesUrl = Link("Issue Tracker"),
            WikiUrl = Link("Wiki"),
            Gallery = pack.Art.Where(a => a.Type == "screenshot").Select(a => new GalleryImage { Url = a.Url, Title = a.Title, Ordering = a.Order }).ToList(),
            PageUrl = $"https://www.feed-the-beast.com/modpacks/{pack.Id}-{Uri.EscapeDataString(pack.Slug)}",
        };
    }

    public async Task<IReadOnlyList<ProjectVersion>> GetVersionsAsync(string projectId, IEnumerable<string>? loaders = null,
        IEnumerable<string>? gameVersions = null, CancellationToken ct = default)
    {
        var pack = await GetPackAsync(ParseProjectId(projectId), ct).ConfigureAwait(false);
        var wantedLoaders = loaders?.ToList() ?? [];
        var wantedVersions = gameVersions?.ToList() ?? [];
        return pack.Versions
            .Where(v => !v.Private)
            .Select(v => ToVersion(pack, v))
            .Where(v => wantedLoaders.Count == 0 || v.Loaders.Intersect(wantedLoaders).Any())
            .Where(v => wantedVersions.Count == 0 || v.GameVersions.Intersect(wantedVersions).Any())
            .OrderByDescending(v => v.DatePublished)
            .ToList();
    }

    public async Task<ProjectVersion> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        var (packId, versionNumber) = ParseVersionId(versionId);
        var pack = await GetPackAsync(packId, ct).ConfigureAwait(false);
        return pack.Versions.FirstOrDefault(v => v.Id == versionNumber) is { } version
            ? ToVersion(pack, version)
            : throw new ModrinthException("Esa versión del modpack ya no existe.");
    }

    private static ProjectVersion ToVersion(FtbPack pack, FtbVersion version) => new()
    {
        Id = $"{Prefix}{pack.Id.ToString(CultureInfo.InvariantCulture)}:{version.Id.ToString(CultureInfo.InvariantCulture)}",
        ProjectId = Id(pack.Id),
        Name = version.Name,
        VersionNumber = version.Name,
        VersionType = version.Type.ToLowerInvariant() is "beta" or "alpha" ? version.Type.ToLowerInvariant() : "release",
        GameVersions = version.Targets.Where(t => t.Type == "game" && t.Name == "minecraft").Select(t => t.Version).ToList(),
        Loaders = version.Targets.Where(t => t.Type == "modloader").Select(t => t.Name.ToLowerInvariant()).ToList(),
        DatePublished = Time(version.Released > 0 ? version.Released : version.Updated),
        // Un modpack de FTB no es un archivo: sus archivos se piden al instalarlo.
        Files = [new VersionFile { Filename = $"{pack.Name} {version.Name}", Primary = true }],
    };

    /// <summary>Lo que lleva una versión: Minecraft, cargador y la lista de archivos con su ruta en la instancia.</summary>
    internal async Task<FtbVersionFiles> GetVersionFilesAsync(int pack, int version, CancellationToken ct) =>
        await GetAsync<FtbVersionFiles>($"/modpack/{pack}/{version}", ct).ConfigureAwait(false);

    // ── Etiquetas ───────────────────────────────────────────────────────────

    public Task<IReadOnlyList<CategoryTag>> GetCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CategoryTag>>([]);

    public Task<IReadOnlyList<LoaderTag>> GetLoadersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LoaderTag>>(new[] { "forge", "fabric", "neoforge", "quilt" }
            .Select(n => new LoaderTag { Name = n, SupportedProjectTypes = ["modpack"] }).ToList());

    /// <summary>Las versiones de Minecraft las da Modrinth (son las mismas en todas las tiendas).</summary>
    public Task<IReadOnlyList<GameVersionTag>> GetGameVersionsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Las versiones de Minecraft se piden a Modrinth.");

    // ── HTTP ────────────────────────────────────────────────────────────────

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ModrinthClient.RequestTimeout);
        try
        {
            using var response = await _httpFactory.CreateClient(ModrinthClient.HttpClientName).GetAsync(BaseUrl + path, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var value = await response.Content.ReadFromJsonAsync<T>(Json, timeout.Token).ConfigureAwait(false);
            if (value is FtbStatus { Status: "error" } error)
                throw new ModrinthException(error.Message is { Length: > 0 } m ? $"FTB: {m}" : "FTB no encuentra ese modpack.");
            return value ?? throw new ModrinthException("FTB devolvió una respuesta vacía.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModrinthException("FTB no responde. Comprueba la conexión a Internet.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Error consultando FTB: {Path}", path);
            throw new ModrinthException(ex.StatusCode is { } code
                ? $"FTB respondió con un error ({(int)code})."
                : "No se pudo conectar con FTB. Comprueba la conexión a Internet.", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Respuesta no válida de FTB: {Path}", path);
            throw new ModrinthException("FTB devolvió una respuesta no válida.", ex);
        }
    }
}

// ── Modelos de la API de FTB ────────────────────────────────────────────────

internal record FtbStatus
{
    public string? Status { get; init; }
    public string? Message { get; init; }
}

internal sealed record FtbList : FtbStatus
{
    public IReadOnlyList<int> Packs { get; init; } = [];
}

internal sealed record FtbPack : FtbStatus
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Synopsis { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public long Released { get; init; }
    public long Updated { get; init; }
    public bool Private { get; init; }
    public long Installs { get; init; }
    public long Plays { get; init; }
    public IReadOnlyList<FtbArt> Art { get; init; } = [];
    public IReadOnlyList<FtbTag> Tags { get; init; } = [];
    public IReadOnlyList<FtbLink> Links { get; init; } = [];
    public IReadOnlyList<FtbAuthor> Authors { get; init; } = [];
    public IReadOnlyList<FtbVersion> Versions { get; init; } = [];
}

internal sealed record FtbArt
{
    public string Url { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Title { get; init; }
    public int Order { get; init; }
}

internal sealed record FtbTag
{
    public string Name { get; init; } = string.Empty;
}

internal sealed record FtbLink
{
    public string Name { get; init; } = string.Empty;
    public string Link { get; init; } = string.Empty;
}

internal sealed record FtbAuthor
{
    public string Name { get; init; } = string.Empty;
}

internal sealed record FtbVersion
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = "release";
    public long Released { get; init; }
    public long Updated { get; init; }
    public bool Private { get; init; }
    public IReadOnlyList<FtbTarget> Targets { get; init; } = [];
}

internal sealed record FtbTarget
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
}

internal sealed record FtbVersionFiles : FtbStatus
{
    public IReadOnlyList<FtbTarget> Targets { get; init; } = [];
    public IReadOnlyList<FtbFile> Files { get; init; } = [];
}

internal sealed record FtbFile
{
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? Sha1 { get; init; }
    public FileHashes? Hashes { get; init; }
    public long Size { get; init; }
    public bool Serveronly { get; init; }
    public bool Clientonly { get; init; }
}
