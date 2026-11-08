using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Modrinth;

/// <summary>
/// Cliente de la API v2 de Modrinth (solo lectura, sin cuenta). Modrinth pide identificar el programa
/// en el User-Agent; lo pone el HttpClient con nombre <see cref="HttpClientName"/>.
/// </summary>
public sealed class ModrinthClient
{
    public const string HttpClientName = "modrinth";
    public const string DefaultBaseUrl = "https://api.modrinth.com/v2";

    /// <summary>Tiempo máximo de cada consulta a la API (las descargas tienen el suyo).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _tagsLock = new(1, 1);
    private IReadOnlyList<CategoryTag>? _categories;
    private IReadOnlyList<LoaderTag>? _loaders;
    private IReadOnlyList<GameVersionTag>? _gameVersions;

    public ModrinthClient(IHttpClientFactory httpFactory, ILogger<ModrinthClient>? logger = null, string baseUrl = DefaultBaseUrl)
    {
        _httpFactory = httpFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string BaseUrl { get; }

    internal HttpClient Http => _httpFactory.CreateClient(HttpClientName);

    public Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct = default) =>
        GetAsync<SearchPage>("/search?" + query.ToQueryString(), ct);

    /// <summary>
    /// Versiones de un proyecto, de la más reciente a la más antigua. Sin las notas de cambios: el
    /// launcher no las muestra y en proyectos grandes son más de la mitad de la respuesta.
    /// </summary>
    public Task<IReadOnlyList<ProjectVersion>> GetVersionsAsync(string projectId, IEnumerable<string>? loaders = null,
        IEnumerable<string>? gameVersions = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (loaders?.ToList() is { Count: > 0 } l)
            query.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(l)));
        if (gameVersions?.ToList() is { Count: > 0 } g)
            query.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(g)));
        query.Add("include_changelog=false");
        var path = $"/project/{Uri.EscapeDataString(projectId)}/version?" + string.Join('&', query);
        return GetAsync<IReadOnlyList<ProjectVersion>>(path, ct);
    }

    public Task<ProjectDetails> GetProjectAsync(string projectId, CancellationToken ct = default) =>
        GetAsync<ProjectDetails>($"/project/{Uri.EscapeDataString(projectId)}", ct);

    public Task<ProjectVersion> GetVersionAsync(string versionId, CancellationToken ct = default) =>
        GetAsync<ProjectVersion>($"/version/{Uri.EscapeDataString(versionId)}", ct);

    /// <summary>Hashes por consulta a <c>/version_files</c> (la API no publica un límite; se va con margen).</summary>
    private const int HashesPerRequest = 200;

    /// <summary>
    /// Busca archivos por su SHA-512 (<c>POST /version_files</c>) y devuelve, para cada hash que está en
    /// Modrinth, la dirección de descarga del archivo con ese mismo hash. Los que no están, no aparecen.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> FindFilesBySha512Async(IEnumerable<string> sha512, CancellationToken ct = default)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hash, version) in await FindVersionsBySha512Async(sha512, ct).ConfigureAwait(false))
        {
            // La respuesta trae la versión entera: se toma el archivo cuyo hash es el pedido.
            if (version.Files.FirstOrDefault(f => string.Equals(f.Hashes.Sha512, hash, StringComparison.OrdinalIgnoreCase)) is { } file
                && ContentInstaller.IsAllowedUrl(file.Url, out _))
                found[hash] = file.Url;
        }

        return found;
    }

    /// <summary>Versión de Modrinth a la que pertenece cada archivo (por su SHA-512). Los que no están, no aparecen.</summary>
    public async Task<IReadOnlyDictionary<string, ProjectVersion>> FindVersionsBySha512Async(IEnumerable<string> sha512, CancellationToken ct = default)
    {
        var found = new Dictionary<string, ProjectVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in sha512.Select(h => h.ToLowerInvariant()).Distinct().Chunk(HashesPerRequest))
        {
            var versions = await PostAsync<Dictionary<string, ProjectVersion>>("/version_files",
                new { hashes = chunk, algorithm = "sha512" }, ct).ConfigureAwait(false);
            foreach (var (hash, version) in versions)
                found[hash] = version;
        }

        return found;
    }

    /// <summary>Varios proyectos a la vez (<c>GET /projects?ids=[…]</c>). Los que no existen, no aparecen.</summary>
    public async Task<IReadOnlyList<ProjectDetails>> GetProjectsAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var result = new List<ProjectDetails>();
        foreach (var chunk in ids.Distinct().Chunk(100))
        {
            var path = "/projects?ids=" + Uri.EscapeDataString(JsonSerializer.Serialize(chunk));
            result.AddRange(await GetAsync<IReadOnlyList<ProjectDetails>>(path, ct).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<IReadOnlyList<CategoryTag>> GetCategoriesAsync(CancellationToken ct = default) =>
        await CachedAsync(() => _categories, v => _categories = v, "/tag/category", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<LoaderTag>> GetLoadersAsync(CancellationToken ct = default) =>
        await CachedAsync(() => _loaders, v => _loaders = v, "/tag/loader", ct).ConfigureAwait(false);

    /// <summary>Versiones de Minecraft, de la más reciente a la más antigua.</summary>
    public async Task<IReadOnlyList<GameVersionTag>> GetGameVersionsAsync(CancellationToken ct = default) =>
        await CachedAsync(() => _gameVersions, v => _gameVersions = v, "/tag/game_version", ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<T>> CachedAsync<T>(Func<IReadOnlyList<T>?> get, Action<IReadOnlyList<T>> set, string path, CancellationToken ct)
    {
        if (get() is { } cached)
            return cached;
        await _tagsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (get() is { } again)
                return again;
            var value = await GetAsync<IReadOnlyList<T>>(path, ct).ConfigureAwait(false);
            set(value);
            return value;
        }
        finally
        {
            _tagsLock.Release();
        }
    }

    private Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        SendAsync(path, token => Http.GetFromJsonAsync<T>(BaseUrl + path, Json, token), ct);

    private Task<T> PostAsync<T>(string path, object body, CancellationToken ct) =>
        SendAsync(path, async token =>
        {
            using var response = await Http.PostAsJsonAsync(BaseUrl + path, body, Json, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(Json, token).ConfigureAwait(false);
        }, ct);

    private async Task<T> SendAsync<T>(string path, Func<CancellationToken, Task<T?>> request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var value = await request(timeout.Token).ConfigureAwait(false);
            return value ?? throw new ModrinthException("Modrinth devolvió una respuesta vacía.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModrinthException("Modrinth no responde. Comprueba la conexión a Internet.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Error consultando Modrinth: {Path}", path);
            throw new ModrinthException(ex.StatusCode is { } code
                ? $"Modrinth respondió con un error ({(int)code})."
                : "No se pudo conectar con Modrinth. Comprueba la conexión a Internet.", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Respuesta no válida de Modrinth: {Path}", path);
            throw new ModrinthException("Modrinth devolvió una respuesta no válida.", ex);
        }
    }
}

/// <summary>Error de Modrinth con un mensaje para mostrar al jugador.</summary>
public sealed class ModrinthException : Exception
{
    public ModrinthException(string message) : base(message) { }
    public ModrinthException(string message, Exception inner) : base(message, inner) { }
}
