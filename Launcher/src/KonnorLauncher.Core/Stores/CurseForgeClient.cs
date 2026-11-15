using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KonnorLauncher.Core.Modrinth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Stores;

/// <summary>
/// Cliente de la API de CurseForge (<c>api.curseforge.com/v1</c>), con los mismos modelos que Modrinth. La API pide
/// una clave de <c>console.curseforge.com</c> (la del jugador). Algunos autores no permiten descargar sus archivos
/// fuera de CurseForge: esas versiones no tienen dirección de descarga.
/// </summary>
public sealed class CurseForgeClient : IContentStore
{
    public const string Prefix = "cf:";
    public const string DefaultBaseUrl = "https://api.curseforge.com/v1";
    public const string KeysPage = "https://console.curseforge.com/";

    /// <summary>Minecraft en CurseForge.</summary>
    private const int MinecraftGameId = 432;

    /// <summary>Clases (tipos de proyecto) de Minecraft en CurseForge.</summary>
    internal const int ClassMods = 6, ClassResourcePacks = 12, ClassModpacks = 4471, ClassShaders = 6552, ClassDataPacks = 6945;

    /// <summary>La API no da más de 10 000 resultados por búsqueda (<c>index + pageSize</c>).</summary>
    private const int MaxResults = 10_000;

    private const int FilesPageSize = 50;
    private const int MaxFilePages = 4;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly Func<string?> _apiKey;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _tagsLock = new(1, 1);
    private IReadOnlyList<CfCategory>? _categories;

    public CurseForgeClient(IHttpClientFactory httpFactory, Func<string?> apiKey, ILogger<CurseForgeClient>? logger = null,
        string baseUrl = DefaultBaseUrl)
    {
        _httpFactory = httpFactory;
        _apiKey = apiKey;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string BaseUrl { get; }
    public StoreKind Kind => StoreKind.CurseForge;
    public string Name => "CurseForge";
    public bool Supports(ContentType type) => true;

    /// <summary>Hay una clave de la API guardada.</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(_apiKey());

    // ── Tipos y cargadores ──────────────────────────────────────────────────

    internal static int ClassOf(ContentType type) => type switch
    {
        ContentType.Modpack => ClassModpacks,
        ContentType.ResourcePack => ClassResourcePacks,
        ContentType.DataPack => ClassDataPacks,
        ContentType.Shader => ClassShaders,
        _ => ClassMods,
    };

    internal static string? ProjectTypeOf(int classId) => classId switch
    {
        ClassMods => "mod",
        ClassModpacks => "modpack",
        ClassResourcePacks => "resourcepack",
        ClassShaders => "shader",
        ClassDataPacks => "datapack",
        _ => null,
    };

    /// <summary>Carpeta de la instancia para un archivo de un modpack, según la clase de su proyecto.</summary>
    internal static string FolderOf(int classId) => classId switch
    {
        ClassResourcePacks => "resourcepacks",
        ClassShaders => "shaderpacks",
        _ => "mods",
    };

    private static readonly (string Name, int Type)[] LoaderTypes = [("forge", 1), ("fabric", 4), ("quilt", 5), ("neoforge", 6)];

    internal static int? LoaderType(string name) => LoaderTypes.FirstOrDefault(l => l.Name == name) is { Type: > 0 } l ? l.Type : null;

    internal static string? LoaderName(int type) => LoaderTypes.FirstOrDefault(l => l.Type == type).Name;

    /// <summary>Un archivo lista en <c>gameVersions</c> tanto versiones de Minecraft como cargadores y lados.</summary>
    private static bool IsMinecraftVersion(string value) => value.Length > 0 && char.IsDigit(value[0]);

    private static string Id(int value) => Prefix + value.ToString(CultureInfo.InvariantCulture);

    private static string VersionId(int modId, int fileId) => $"{Prefix}{modId.ToString(CultureInfo.InvariantCulture)}:{fileId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Número de un proyecto (<c>cf:1234</c> → 1234).</summary>
    public static int ParseProjectId(string projectId) =>
        projectId.StartsWith(Prefix, StringComparison.Ordinal) && int.TryParse(projectId.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new ModrinthException("Proyecto de CurseForge no válido.");

    private static (int ModId, int FileId) ParseVersionId(string versionId)
    {
        var parts = versionId.StartsWith(Prefix, StringComparison.Ordinal) ? versionId[Prefix.Length..].Split(':') : [];
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mod)
                                 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var file)
            ? (mod, file)
            : throw new ModrinthException("Versión de CurseForge no válida.");
    }

    // ── Búsqueda ────────────────────────────────────────────────────────────

    /// <summary>Orden de CurseForge (<c>sortField</c>): popularidad, descargas, valoración, publicación y actualización.</summary>
    internal static int SortField(SearchSort sort) => sort switch
    {
        SearchSort.Downloads => 6,
        SearchSort.Follows => 12,
        SearchSort.Newest => 11,
        SearchSort.Updated => 3,
        _ => 2,
    };

    public async Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 50);
        var index = Math.Min((Math.Max(1, query.Page) - 1) * limit, MaxResults - limit);
        var parts = new List<string>
        {
            "gameId=" + MinecraftGameId,
            "classId=" + ClassOf(query.Type),
            "sortField=" + SortField(query.Sort),
            "sortOrder=desc",
            "index=" + index,
            "pageSize=" + limit,
        };
        if (!string.IsNullOrWhiteSpace(query.Text))
            parts.Add("searchFilter=" + Uri.EscapeDataString(query.Text.Trim()));

        if (query.GameVersions.Count == 1)
            parts.Add("gameVersion=" + Uri.EscapeDataString(query.GameVersions.First()));
        else if (query.GameVersions.Count > 1)
            parts.Add("gameVersions=" + Uri.EscapeDataString(JsonSerializer.Serialize(query.GameVersions.Take(4))));

        var loaders = query.Loaders.Select(LoaderType).OfType<int>().Distinct().ToList();
        if (loaders.Count == 1)
            parts.Add("modLoaderType=" + loaders[0]);
        else if (loaders.Count > 1)
            parts.Add("modLoaderTypes=" + Uri.EscapeDataString(JsonSerializer.Serialize(loaders)));

        if (query.IncludedCategories.Count > 0)
        {
            var categories = await LoadCategoriesAsync(ct).ConfigureAwait(false);
            var classId = ClassOf(query.Type);
            var ids = query.IncludedCategories
                .Select(name => categories.FirstOrDefault(c => c.ClassId == classId && c.Name == name)?.Id)
                .OfType<int>().Take(10).ToList();
            if (ids.Count == 1)
                parts.Add("categoryId=" + ids[0]);
            else if (ids.Count > 1)
                parts.Add("categoryIds=" + Uri.EscapeDataString(JsonSerializer.Serialize(ids)));
        }

        var response = await GetAsync<CfList<CfMod>>("/mods/search?" + string.Join('&', parts), ct).ConfigureAwait(false);
        var hits = response.Data.Where(m => !query.ExcludedCategories.Any(x => m.Categories.Any(c => c.Name == x))).Select(ToHit).ToList();
        return new SearchPage
        {
            Hits = hits,
            Offset = index,
            Limit = limit,
            TotalHits = Math.Min(response.Pagination?.TotalCount ?? hits.Count, MaxResults),
        };
    }

    private static IReadOnlyList<string> LoadersOf(CfMod mod) =>
        mod.LatestFilesIndexes.Select(f => f.ModLoader is { } l ? LoaderName(l) : null).OfType<string>().Distinct().ToList();

    private static IReadOnlyList<string> GameVersionsOf(CfMod mod) =>
        mod.LatestFilesIndexes.Select(f => f.GameVersion).Where(v => v is { Length: > 0 } && IsMinecraftVersion(v)).OfType<string>().Distinct().ToList();

    internal static SearchHit ToHit(CfMod mod)
    {
        var categories = mod.Categories.Select(c => c.Name).Distinct().ToList();
        return new SearchHit
        {
            ProjectId = Id(mod.Id),
            ProjectType = ProjectTypeOf(mod.ClassId ?? 0) ?? "mod",
            Slug = mod.Slug,
            Title = mod.Name,
            Author = mod.Authors.FirstOrDefault()?.Name ?? string.Empty,
            Description = mod.Summary,
            Categories = [.. categories, .. LoadersOf(mod)],
            DisplayCategories = categories,
            Downloads = (long)mod.DownloadCount,
            Follows = mod.ThumbsUpCount,
            IconUrl = mod.Logo?.ThumbnailUrl ?? mod.Logo?.Url,
            DateModified = mod.DateModified,
        };
    }

    // ── Proyecto y versiones ────────────────────────────────────────────────

    public async Task<ProjectDetails> GetProjectAsync(string projectId, CancellationToken ct = default)
    {
        var id = ParseProjectId(projectId);
        var mod = GetAsync<CfItem<CfMod>>($"/mods/{id}", ct);
        var description = GetAsync<CfItem<string>>($"/mods/{id}/description", ct);
        await Task.WhenAll(mod, description).ConfigureAwait(false);
        var m = mod.Result.Data;
        return new ProjectDetails
        {
            Id = Id(m.Id),
            Slug = m.Slug,
            ProjectType = ProjectTypeOf(m.ClassId ?? 0) ?? "mod",
            Title = m.Name,
            Description = m.Summary,
            Body = description.Result.Data,
            Categories = m.Categories.Select(c => c.Name).Distinct().ToList(),
            Loaders = LoadersOf(m),
            GameVersions = GameVersionsOf(m),
            Downloads = (long)m.DownloadCount,
            Followers = m.ThumbsUpCount,
            IconUrl = m.Logo?.ThumbnailUrl ?? m.Logo?.Url,
            Published = m.DateReleased,
            Updated = m.DateModified,
            IssuesUrl = m.Links?.IssuesUrl,
            SourceUrl = m.Links?.SourceUrl,
            WikiUrl = m.Links?.WikiUrl,
            Gallery = m.Screenshots.Select((s, i) => new GalleryImage { Url = s.Url, Title = s.Title, Description = s.Description, Ordering = i }).ToList(),
            PageUrl = m.Links?.WebsiteUrl ?? $"https://www.curseforge.com/minecraft/{Uri.EscapeDataString(m.Slug)}",
        };
    }

    public async Task<IReadOnlyList<ProjectVersion>> GetVersionsAsync(string projectId, IEnumerable<string>? loaders = null,
        IEnumerable<string>? gameVersions = null, CancellationToken ct = default)
    {
        var id = ParseProjectId(projectId);
        // Solo los cargadores de mods filtran (los de Modrinth para paquetes de recursos o data packs no existen aquí).
        var wantedLoaders = loaders?.Where(l => LoaderType(l) is not null).ToList() ?? [];
        var wantedVersions = gameVersions?.ToList() ?? [];
        var filter = new List<string>();
        if (wantedVersions.Count == 1)
            filter.Add("gameVersion=" + Uri.EscapeDataString(wantedVersions[0]));
        if (wantedLoaders.Count == 1)
            filter.Add("modLoaderType=" + LoaderType(wantedLoaders[0]));

        var files = new List<CfFile>();
        for (var page = 0; page < MaxFilePages; page++)
        {
            var path = $"/mods/{id}/files?" + string.Join('&', filter.Append($"index={page * FilesPageSize}").Append($"pageSize={FilesPageSize}"));
            var response = await GetAsync<CfList<CfFile>>(path, ct).ConfigureAwait(false);
            files.AddRange(response.Data);
            if (response.Data.Count < FilesPageSize || files.Count >= (response.Pagination?.TotalCount ?? 0))
                break;
        }

        return files
            .Where(f => !f.IsServerPack)
            .Select(ToVersion)
            .Where(v => wantedLoaders.Count == 0 || v.Loaders.Count == 0 || v.Loaders.Intersect(wantedLoaders).Any())
            .Where(v => wantedVersions.Count == 0 || v.GameVersions.Intersect(wantedVersions).Any())
            .OrderByDescending(v => v.DatePublished)
            .ToList();
    }

    public async Task<ProjectVersion> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        var (modId, fileId) = ParseVersionId(versionId);
        return ToVersion((await GetAsync<CfItem<CfFile>>($"/mods/{modId}/files/{fileId}", ct).ConfigureAwait(false)).Data);
    }

    internal static ProjectVersion ToVersion(CfFile file) => new()
    {
        Id = VersionId(file.ModId, file.Id),
        ProjectId = Id(file.ModId),
        Name = file.DisplayName,
        VersionNumber = file.DisplayName,
        VersionType = file.ReleaseType switch { 2 => "beta", 3 => "alpha", _ => "release" },
        GameVersions = file.GameVersions.Where(IsMinecraftVersion).Distinct().ToList(),
        Loaders = file.GameVersions.Select(v => v.ToLowerInvariant()).Where(v => LoaderType(v) is not null).Distinct().ToList(),
        DatePublished = file.FileDate,
        Downloads = file.DownloadCount,
        Files =
        [
            new VersionFile
            {
                // Sin dirección: el autor no permite descargarlo fuera de CurseForge.
                Url = file.DownloadUrl ?? string.Empty,
                Filename = file.FileName,
                Primary = true,
                Size = file.FileLength,
                Hashes = new FileHashes { Sha1 = file.Hashes.FirstOrDefault(h => h.Algo == 1)?.Value },
            },
        ],
        Dependencies = file.Dependencies
            .Where(d => d.RelationType == 3)
            .Select(d => new VersionDependency { ProjectId = Id(d.ModId), DependencyType = "required" })
            .ToList(),
    };

    /// <summary>Archivos por su número (<c>POST /mods/files</c>), para los modpacks.</summary>
    internal async Task<IReadOnlyList<CfFile>> GetFilesAsync(IEnumerable<int> fileIds, CancellationToken ct)
    {
        var result = new List<CfFile>();
        foreach (var chunk in fileIds.Distinct().Chunk(500))
            result.AddRange((await PostAsync<CfList<CfFile>>("/mods/files", new { fileIds = chunk }, ct).ConfigureAwait(false)).Data);
        return result;
    }

    /// <summary>Proyectos por su número (<c>POST /mods</c>), para saber la carpeta de cada archivo de un modpack.</summary>
    internal async Task<IReadOnlyList<CfMod>> GetModsAsync(IEnumerable<int> modIds, CancellationToken ct)
    {
        var result = new List<CfMod>();
        foreach (var chunk in modIds.Distinct().Chunk(500))
            result.AddRange((await PostAsync<CfList<CfMod>>("/mods", new { modIds = chunk }, ct).ConfigureAwait(false)).Data);
        return result;
    }

    // ── Etiquetas ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CategoryTag>> GetCategoriesAsync(CancellationToken ct = default) =>
        (await LoadCategoriesAsync(ct).ConfigureAwait(false))
        .Where(c => !c.IsClass && ProjectTypeOf(c.ClassId ?? 0) is not null)
        .Select(c => new CategoryTag { Name = c.Name, ProjectType = ProjectTypeOf(c.ClassId ?? 0)!, Header = "categories" })
        .DistinctBy(c => (c.Name, c.ProjectType))
        .ToList();

    public Task<IReadOnlyList<LoaderTag>> GetLoadersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LoaderTag>>(LoaderTypes
            .Select(l => new LoaderTag { Name = l.Name, SupportedProjectTypes = ["mod", "modpack"] }).ToList());

    /// <summary>Las versiones de Minecraft las da Modrinth (son las mismas en todas las tiendas).</summary>
    public Task<IReadOnlyList<GameVersionTag>> GetGameVersionsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Las versiones de Minecraft se piden a Modrinth.");

    private async Task<IReadOnlyList<CfCategory>> LoadCategoriesAsync(CancellationToken ct)
    {
        if (_categories is { } cached)
            return cached;
        await _tagsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _categories ??= (await GetAsync<CfList<CfCategory>>($"/categories?gameId={MinecraftGameId}", ct).ConfigureAwait(false)).Data;
        }
        finally
        {
            _tagsLock.Release();
        }
    }

    // ── HTTP ────────────────────────────────────────────────────────────────

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new ModrinthException("Falta la clave de la API de CurseForge (Opciones → Tienda).");
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.TryAddWithoutValidation("x-api-key", key.Trim());
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(path, () => Request(HttpMethod.Get, path), ct);

    private Task<T> PostAsync<T>(string path, object body, CancellationToken ct) => SendAsync<T>(path, () =>
    {
        var request = Request(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body, options: Json);
        return request;
    }, ct);

    private async Task<T> SendAsync<T>(string path, Func<HttpRequestMessage> build, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ModrinthClient.RequestTimeout);
        try
        {
            using var request = build();
            using var response = await _httpFactory.CreateClient(ModrinthClient.HttpClientName).SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new ModrinthException("CurseForge no acepta la clave de la API. Revísala en Opciones → Tienda.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ModrinthException("CurseForge no encuentra ese proyecto.");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(Json, timeout.Token).ConfigureAwait(false)
                   ?? throw new ModrinthException("CurseForge devolvió una respuesta vacía.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModrinthException("CurseForge no responde. Comprueba la conexión a Internet.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Error consultando CurseForge: {Path}", path);
            throw new ModrinthException(ex.StatusCode is { } code
                ? $"CurseForge respondió con un error ({(int)code})."
                : "No se pudo conectar con CurseForge. Comprueba la conexión a Internet.", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Respuesta no válida de CurseForge: {Path}", path);
            throw new ModrinthException("CurseForge devolvió una respuesta no válida.", ex);
        }
    }
}

// ── Modelos de la API de CurseForge ─────────────────────────────────────────

internal sealed record CfList<T>
{
    public IReadOnlyList<T> Data { get; init; } = [];
    public CfPagination? Pagination { get; init; }
}

internal sealed record CfItem<T>
{
    public T Data { get; init; } = default!;
}

internal sealed record CfPagination
{
    public int Index { get; init; }
    public int PageSize { get; init; }
    public int ResultCount { get; init; }
    public int TotalCount { get; init; }
}

internal sealed record CfCategory
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public int? ClassId { get; init; }
    public bool IsClass { get; init; }
}

internal sealed record CfMod
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public double DownloadCount { get; init; }
    public long ThumbsUpCount { get; init; }
    public int? ClassId { get; init; }
    public CfLinks? Links { get; init; }
    public CfLogo? Logo { get; init; }
    public IReadOnlyList<CfCategory> Categories { get; init; } = [];
    public IReadOnlyList<CfAuthor> Authors { get; init; } = [];
    public IReadOnlyList<CfScreenshot> Screenshots { get; init; } = [];
    public IReadOnlyList<CfFileIndex> LatestFilesIndexes { get; init; } = [];
    public DateTimeOffset DateModified { get; init; }
    public DateTimeOffset DateReleased { get; init; }
}

internal sealed record CfLinks
{
    public string? WebsiteUrl { get; init; }
    public string? WikiUrl { get; init; }
    public string? IssuesUrl { get; init; }
    public string? SourceUrl { get; init; }
}

internal sealed record CfLogo
{
    public string? ThumbnailUrl { get; init; }
    public string? Url { get; init; }
}

internal sealed record CfAuthor
{
    public string Name { get; init; } = string.Empty;
}

internal sealed record CfScreenshot
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string Url { get; init; } = string.Empty;
}

internal sealed record CfFileIndex
{
    public string? GameVersion { get; init; }
    public int FileId { get; init; }
    public int? ModLoader { get; init; }
}

internal sealed record CfFile
{
    public int Id { get; init; }
    public int ModId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public int ReleaseType { get; init; }
    public DateTimeOffset FileDate { get; init; }
    public long FileLength { get; init; }
    public long DownloadCount { get; init; }
    public string? DownloadUrl { get; init; }
    public bool IsServerPack { get; init; }
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<CfHash> Hashes { get; init; } = [];
    public IReadOnlyList<CfDependency> Dependencies { get; init; } = [];
}

internal sealed record CfHash
{
    public string Value { get; init; } = string.Empty;
    public int Algo { get; init; }
}

internal sealed record CfDependency
{
    public int ModId { get; init; }
    public int RelationType { get; init; }
}
