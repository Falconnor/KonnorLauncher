using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Servers;

/// <summary>Estado de un servidor en este momento. <paramref name="Icon"/>: PNG del icono, si tiene.</summary>
public sealed record ServerStatus(bool Online, int PlayersOnline, int PlayersMax, string? Version, string? Motd, byte[]? Icon);

/// <summary>
/// Estado en vivo de un servidor por su dirección (jugadores, versión, mensaje e icono) con la API pública
/// de mcstatus.io. Sin clave; la respuesta la guardan ellos en caché un minuto.
/// </summary>
public sealed class ServerStatusClient
{
    public const string HttpClientName = "mcstatus";
    public const string DefaultBaseUrl = "https://api.mcstatus.io/v2";

    /// <summary>Tope del icono (64×64 en PNG ocupa unos pocos KB).</summary>
    private const int MaxIconBytes = 256 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;

    public ServerStatusClient(IHttpClientFactory httpFactory, ILogger<ServerStatusClient>? logger = null, string baseUrl = DefaultBaseUrl)
    {
        _httpFactory = httpFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string BaseUrl { get; }

    /// <summary>Estado del servidor; null si no se pudo consultar (sin conexión o la API no responde).</summary>
    public async Task<ServerStatus?> GetAsync(ServerAddress address, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var url = $"{BaseUrl}/status/java/{Uri.EscapeDataString(address.ToString())}";
        try
        {
            var dto = await _httpFactory.CreateClient(HttpClientName).GetFromJsonAsync<StatusDto>(url, Json, timeout.Token).ConfigureAwait(false);
            if (dto is null)
                return null;
            if (!dto.Online)
                return new ServerStatus(false, 0, 0, null, null, null);
            return new ServerStatus(true, dto.Players?.Online ?? 0, dto.Players?.Max ?? 0,
                dto.Version?.NameClean, dto.Motd?.Clean?.Trim(), DecodeIcon(dto.Icon));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogInformation(ex, "No se pudo consultar el estado de {Address}", address);
            return null;
        }
    }

    /// <summary>El icono llega como <c>data:image/png;base64,...</c>. Cualquier otra cosa se descarta.</summary>
    public static byte[]? DecodeIcon(string? dataUrl)
    {
        const string prefix = "data:image/png;base64,";
        if (dataUrl is null || !dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            || dataUrl.Length - prefix.Length > MaxIconBytes * 4 / 3 + 4)
            return null;
        try
        {
            var bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
            // Cabecera PNG
            return bytes is [0x89, 0x50, 0x4E, 0x47, ..] ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record StatusDto(bool Online, VersionDto? Version, PlayersDto? Players, MotdDto? Motd, string? Icon);

    private sealed record VersionDto([property: JsonPropertyName("name_clean")] string? NameClean);

    private sealed record PlayersDto(int Online, int Max);

    private sealed record MotdDto(string? Clean);
}
