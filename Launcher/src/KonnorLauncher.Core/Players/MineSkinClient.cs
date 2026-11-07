using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Players;

/// <summary>Skin de la galería (lo que trae el listado).</summary>
/// <param name="Texture">Hash de la textura en los servidores de Mojang (textures.minecraft.net).</param>
public sealed record SkinSummary(string Uuid, string? Name, string Texture, long Timestamp);

/// <summary>Una página de la galería. <paramref name="NextAfter"/>: cursor de la siguiente (null = no hay más).</summary>
public sealed record SkinPage(IReadOnlyList<SkinSummary> Skins, string? NextAfter);

/// <summary>Ficha de una skin. <paramref name="Slim"/>: modelo de brazos finos (Alex).</summary>
public sealed record SkinDetails(string Uuid, string? Name, string Texture, bool Slim, long Views);

/// <summary>
/// Orden de la galería: las más recientes o las más vistas últimamente (<c>/skins/popular</c>, el mismo
/// listado que la sección «Popular» de mineskin.org).
/// </summary>
public enum SkinListOrder
{
    Latest,
    Popular,
}

/// <summary>Error de la galería de skins con un mensaje apto para mostrar.</summary>
public sealed class MineSkinException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Galería pública de MineSkin (API v2, solo lectura y sin clave). Las texturas se descargan de los
/// servidores de Mojang por su hash. MineSkin pide identificar el programa en el User-Agent; lo pone el
/// HttpClient con nombre <see cref="HttpClientName"/>.
/// </summary>
public sealed partial class MineSkinClient
{
    public const string HttpClientName = "mineskin";
    public const string DefaultBaseUrl = "https://api.mineskin.org/v2";
    public const string DefaultTexturesUrl = "https://textures.minecraft.net/texture";

    /// <summary>Skins por página (la API acepta hasta 100).</summary>
    public const int PageSize = 48;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;

    public MineSkinClient(IHttpClientFactory httpFactory, ILogger<MineSkinClient>? logger = null,
        string baseUrl = DefaultBaseUrl, string texturesUrl = DefaultTexturesUrl)
    {
        _httpFactory = httpFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        BaseUrl = baseUrl.TrimEnd('/');
        TexturesUrl = texturesUrl.TrimEnd('/');
    }

    public string BaseUrl { get; }
    public string TexturesUrl { get; }

    /// <summary>Página web de la skin en MineSkin (atribución).</summary>
    public static string PageUrl(string uuid) => $"https://mineskin.org/skins/{Uri.EscapeDataString(uuid)}";

    /// <summary>
    /// Galería en el orden <paramref name="order"/>; <paramref name="filter"/> busca por nombre (y etiquetas).
    /// <para>
    /// El listado de populares repite skins de una página a otra (quien lo use debe quitar las repetidas)
    /// y, al buscar, devuelve resultados que no coinciden con lo buscado: la búsqueda es fiable solo en
    /// <see cref="SkinListOrder.Latest"/>.
    /// </para>
    /// </summary>
    public async Task<SkinPage> ListAsync(string? filter = null, string? after = null, int size = PageSize,
        SkinListOrder order = SkinListOrder.Latest, CancellationToken ct = default)
    {
        var query = new List<string> { "size=" + Math.Clamp(size, 1, 100) };
        if (!string.IsNullOrWhiteSpace(filter))
            query.Add("filter=" + Uri.EscapeDataString(filter.Trim()));
        if (!string.IsNullOrEmpty(after))
            query.Add("after=" + Uri.EscapeDataString(after));
        var list = order == SkinListOrder.Popular ? "/skins/popular?" : "/skins?";
        var dto = await GetAsync<ListDto>(list + string.Join('&', query), ct).ConfigureAwait(false);
        var skins = (dto.Skins ?? [])
            .Where(s => !string.IsNullOrEmpty(s.Uuid) && IsTextureHash(s.Texture))
            .Select(s => new SkinSummary(s.Uuid!, string.IsNullOrWhiteSpace(s.Name) ? null : s.Name.Trim(), s.Texture!, s.Timestamp))
            .ToList();
        return new SkinPage(skins, dto.Pagination?.Next?.After);
    }

    public async Task<SkinDetails> GetAsync(string uuid, CancellationToken ct = default)
    {
        var dto = await GetAsync<DetailsDto>("/skins/" + Uri.EscapeDataString(uuid), ct).ConfigureAwait(false);
        var skin = dto.Skin ?? throw new MineSkinException("La skin no existe.");
        var texture = skin.Texture?.Hash?.Skin;
        if (!IsTextureHash(texture))
            throw new MineSkinException("La skin no tiene una textura válida.");
        return new SkinDetails(skin.Uuid ?? uuid, string.IsNullOrWhiteSpace(skin.Name) ? null : skin.Name.Trim(), texture!,
            string.Equals(skin.Variant, "slim", StringComparison.OrdinalIgnoreCase), skin.Views);
    }

    /// <summary>
    /// Descarga la textura (PNG) a <paramref name="path"/> pasando por un temporal: nunca queda a medias.
    /// Como mucho <see cref="SkinTexture.MaxFileBytes"/>.
    /// </summary>
    public async Task DownloadTextureAsync(string texture, string path, CancellationToken ct = default)
    {
        if (!IsTextureHash(texture))
            throw new MineSkinException("La textura no es válida.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var temp = path + ".tmp";
        try
        {
            using var response = await Http.GetAsync($"{TexturesUrl}/{texture}", HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new MineSkinException($"No se pudo descargar la skin (HTTP {(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength > SkinTexture.MaxFileBytes)
                throw new MineSkinException("La skin es demasiado grande.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[16 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > SkinTexture.MaxFileBytes)
                        throw new MineSkinException("La skin es demasiado grande.");
                    await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "No se pudo descargar la textura {Texture}", texture);
            throw new MineSkinException("No se pudo descargar la skin. Comprueba la conexión.", ex);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Hash hexadecimal: lo único que se acepta para formar la dirección de la textura.</summary>
    public static bool IsTextureHash(string? value) => value is { Length: >= 32 and <= 128 } && TextureHash().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]+$")]
    private static partial Regex TextureHash();

    private HttpClient Http => _httpFactory.CreateClient(HttpClientName);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await Http.GetAsync(BaseUrl + path, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode == 429)
                throw new MineSkinException("MineSkin recibe demasiadas consultas. Prueba de nuevo en un momento.");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new MineSkinException("La skin no existe.");
            if (!response.IsSuccessStatusCode)
                throw new MineSkinException($"MineSkin no responde (HTTP {(int)response.StatusCode}).");
            return await response.Content.ReadFromJsonAsync<T>(Json, timeout.Token).ConfigureAwait(false)
                   ?? throw new MineSkinException("Respuesta vacía de MineSkin.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Error al consultar MineSkin: {Path}", path);
            throw new MineSkinException(ex is JsonException
                ? "Respuesta no válida de MineSkin."
                : "No se pudo conectar con MineSkin. Comprueba la conexión.", ex);
        }
    }

    // ── Formato de la API ─────────────────────────────────────────────────────

    private sealed record ListDto(List<SummaryDto>? Skins, PaginationDto? Pagination);

    private sealed record SummaryDto(string? Uuid, string? Name, string? Texture, long Timestamp);

    private sealed record PaginationDto(CursorDto? Next);

    private sealed record CursorDto(string? After);

    private sealed record DetailsDto(SkinDto? Skin);

    private sealed record SkinDto(string? Uuid, string? Name, string? Variant, TextureDto? Texture, long Views);

    private sealed record TextureDto(HashDto? Hash);

    private sealed record HashDto([property: JsonPropertyName("skin")] string? Skin);
}
