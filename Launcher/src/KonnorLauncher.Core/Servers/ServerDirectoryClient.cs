using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KonnorLauncher.Core.Modrinth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Servers;

/// <summary>Orden del directorio de servidores.</summary>
public enum ServerSort
{
    /// <summary>Más jugadores conectados ahora.</summary>
    Players,

    /// <summary>Más partidas verificadas en las últimas dos semanas.</summary>
    Plays,
    Relevance,
    Newest,
}

/// <summary>Lo que el servidor pide al cliente: un modpack de Modrinth, Minecraft vanilla o nada declarado.</summary>
public enum ServerContentKind
{
    None,
    Modpack,
    Vanilla,
}

/// <summary>Búsqueda en el directorio. Los filtros vacíos no filtran.</summary>
public sealed record ServerQuery
{
    public string? Text { get; init; }
    public ServerSort Sort { get; init; } = ServerSort.Players;
    public string? GameVersion { get; init; }
    public string? Category { get; init; }
    public string? Region { get; init; }
    public string? Language { get; init; }

    /// <summary>
    /// true: solo servidores en modo offline (los únicos a los que se entra sin cuenta premium); false: solo
    /// los que piden premium; null: todos.
    /// </summary>
    public bool? OfflineMode { get; init; }

    public int Offset { get; init; }
    public int Limit { get; init; } = ServerDirectoryClient.PageSize;
}

/// <summary>Un servidor del directorio de Modrinth.</summary>
public sealed record ServerListing
{
    public required string ProjectId { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? IconUrl { get; init; }
    public required ServerAddress Address { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> GameVersions { get; init; } = [];

    /// <summary>Con datos de la última comprobación de Modrinth; null si no respondió.</summary>
    public int? PlayersOnline { get; init; }
    public int? PlayersMax { get; init; }
    public string? Region { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = [];
    public int PlaysTwoWeeks { get; init; }
    public ServerContentKind ContentKind { get; init; }

    /// <summary>Modpack que pide (proyecto y versión de Modrinth), si <see cref="ContentKind"/> es Modpack.</summary>
    public string? ModpackProjectId { get; init; }
    public string? ModpackVersionId { get; init; }

    /// <summary>Versión de Minecraft recomendada (vanilla) o la más reciente que declara.</summary>
    public string? RecommendedVersion { get; init; }

    public bool IsOnline => PlayersOnline is not null;

    /// <summary>Acepta jugadores sin cuenta premium (categoría <c>offline-mode</c>).</summary>
    public bool AllowsOfflinePlayers => Categories.Contains(ServerDirectoryClient.OfflineModeCategory);

    public string PageUrl => $"https://modrinth.com/server/{Uri.EscapeDataString(Slug)}";
}

public sealed record ServerPage(IReadOnlyList<ServerListing> Servers, int Total);

/// <summary>
/// Directorio de servidores de Minecraft Java de Modrinth. Usa la búsqueda de la API v3: es la que trae
/// la dirección del servidor, los jugadores conectados (Modrinth lo comprueba cada poco), la región, los
/// idiomas y el modpack que pide. La v3 no está documentada como la v2: si cambia, este es el único sitio
/// que hay que ajustar. Los servidores sin una dirección válida se descartan.
/// </summary>
public sealed class ServerDirectoryClient
{
    public const string DefaultBaseUrl = "https://api.modrinth.com/v3";
    public const int PageSize = 30;
    public const string OfflineModeCategory = "offline-mode";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;

    public ServerDirectoryClient(IHttpClientFactory httpFactory, ILogger<ServerDirectoryClient>? logger = null, string baseUrl = DefaultBaseUrl)
    {
        _httpFactory = httpFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string BaseUrl { get; }

    /// <summary>Parámetros de la búsqueda (facetas: cada grupo se combina con Y).</summary>
    public static string BuildQueryString(ServerQuery query)
    {
        var facets = new List<string[]> { new[] { "project_types:minecraft_java_server" } };
        if (!string.IsNullOrWhiteSpace(query.GameVersion))
            facets.Add([$"game_versions:{query.GameVersion.Trim()}"]);
        if (!string.IsNullOrWhiteSpace(query.Category))
            facets.Add([$"categories:{query.Category.Trim()}"]);
        if (query.OfflineMode == true && query.Category != OfflineModeCategory)
            facets.Add([$"categories:{OfflineModeCategory}"]);
        else if (query.OfflineMode == false)
            facets.Add([$"categories!={OfflineModeCategory}"]);
        if (!string.IsNullOrWhiteSpace(query.Region))
            facets.Add([$"minecraft_server.region:{query.Region.Trim()}"]);
        if (!string.IsNullOrWhiteSpace(query.Language))
            facets.Add([$"minecraft_server.languages:{query.Language.Trim()}"]);

        var index = query.Sort switch
        {
            ServerSort.Players => "minecraft_java_server.ping.data.players_online",
            ServerSort.Plays => "minecraft_java_server.verified_plays_2w",
            ServerSort.Newest => "newest",
            _ => "relevance",
        };
        var parts = new List<string>
        {
            "facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(facets)),
            "index=" + Uri.EscapeDataString(index),
            "offset=" + Math.Max(0, query.Offset),
            "limit=" + Math.Clamp(query.Limit, 1, 100),
        };
        if (!string.IsNullOrWhiteSpace(query.Text))
            parts.Insert(0, "query=" + Uri.EscapeDataString(query.Text.Trim()));
        return string.Join('&', parts);
    }

    public async Task<ServerPage> SearchAsync(ServerQuery query, CancellationToken ct = default)
    {
        var path = "/search?" + BuildQueryString(query);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        SearchDto dto;
        try
        {
            dto = await _httpFactory.CreateClient(ModrinthClient.HttpClientName)
                      .GetFromJsonAsync<SearchDto>(BaseUrl + path, Json, timeout.Token).ConfigureAwait(false)
                  ?? throw new ModrinthException("Modrinth devolvió una respuesta vacía.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModrinthException("Modrinth no responde. Comprueba la conexión a Internet.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Error consultando los servidores de Modrinth: {Path}", path);
            throw new ModrinthException(ex.StatusCode is { } code
                ? $"Modrinth respondió con un error ({(int)code})."
                : "No se pudo conectar con Modrinth. Comprueba la conexión a Internet.", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Respuesta no válida de los servidores de Modrinth: {Path}", path);
            throw new ModrinthException("Modrinth devolvió una respuesta no válida.", ex);
        }

        var servers = (dto.Hits ?? []).Select(ToListing).OfType<ServerListing>().ToList();
        return new ServerPage(servers, dto.TotalHits);
    }

    private static ServerListing? ToListing(HitDto hit)
    {
        var java = hit.JavaServer;
        if (string.IsNullOrEmpty(hit.ProjectId) || string.IsNullOrWhiteSpace(hit.Name)
            || !ServerAddress.TryParse(java?.Address, out var address))
            return null;
        var ping = java?.Ping?.Data;
        var content = java?.Content;
        var kind = content?.Kind switch
        {
            "modpack" when !string.IsNullOrEmpty(content.ProjectId) => ServerContentKind.Modpack,
            "vanilla" => ServerContentKind.Vanilla,
            _ => ServerContentKind.None,
        };
        var versions = hit.GameVersions ?? [];
        return new ServerListing
        {
            ProjectId = hit.ProjectId,
            Slug = string.IsNullOrEmpty(hit.Slug) ? hit.ProjectId : hit.Slug,
            Name = hit.Name.Trim(),
            Summary = hit.Summary?.Trim() ?? string.Empty,
            IconUrl = string.IsNullOrWhiteSpace(hit.IconUrl) ? null : hit.IconUrl,
            Address = address,
            Categories = hit.Categories ?? [],
            GameVersions = versions,
            PlayersOnline = ping?.PlayersOnline,
            PlayersMax = ping?.PlayersMax,
            Region = hit.Server?.Region,
            Languages = hit.Server?.Languages ?? [],
            PlaysTwoWeeks = java?.VerifiedPlays2W ?? 0,
            ContentKind = kind,
            ModpackProjectId = kind == ServerContentKind.Modpack ? content!.ProjectId : null,
            ModpackVersionId = kind == ServerContentKind.Modpack ? content!.VersionId : null,
            RecommendedVersion = content?.RecommendedGameVersion
                                 ?? content?.SupportedGameVersions?.FirstOrDefault()
                                 ?? versions.LastOrDefault(),
        };
    }

    // ── Formato de la API v3 ──────────────────────────────────────────────

    private sealed record SearchDto(List<HitDto>? Hits, [property: JsonPropertyName("total_hits")] int TotalHits);

    private sealed record HitDto(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        string? Slug,
        string? Name,
        string? Summary,
        [property: JsonPropertyName("icon_url")] string? IconUrl,
        List<string>? Categories,
        [property: JsonPropertyName("game_versions")] List<string>? GameVersions,
        [property: JsonPropertyName("minecraft_server")] ServerDto? Server,
        [property: JsonPropertyName("minecraft_java_server")] JavaServerDto? JavaServer);

    private sealed record ServerDto(string? Region, List<string>? Languages);

    private sealed record JavaServerDto(
        string? Address,
        ContentDto? Content,
        PingDto? Ping,
        [property: JsonPropertyName("verified_plays_2w")] int? VerifiedPlays2W);

    private sealed record ContentDto(
        string? Kind,
        [property: JsonPropertyName("version_id")] string? VersionId,
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("supported_game_versions")] List<string>? SupportedGameVersions,
        [property: JsonPropertyName("recommended_game_version")] string? RecommendedGameVersion);

    private sealed record PingDto(PingDataDto? Data);

    private sealed record PingDataDto(
        [property: JsonPropertyName("players_online")] int? PlayersOnline,
        [property: JsonPropertyName("players_max")] int? PlayersMax);
}
