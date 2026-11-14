using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KonnorLauncher.Core.Hosting.Exaroton;

/// <summary>Estados de un servidor de exaroton (los números de su API).</summary>
public enum ExarotonStatus
{
    Offline = 0,
    Online = 1,
    Starting = 2,
    Stopping = 3,
    Restarting = 4,
    Saving = 5,
    Loading = 6,
    Crashed = 7,
    Pending = 8,
    Transferring = 9,
    Preparing = 10,
}

public sealed record ExarotonPlayers(int Max, int Count, IReadOnlyList<string>? List);

public sealed record ExarotonSoftware(string? Id, string? Name, string? Version, string? Type);

/// <summary>Un servidor de exaroton tal como lo devuelve su API.</summary>
public sealed record ExarotonServer(
    string Id,
    string Name,
    string Address,
    string? Motd,
    ExarotonStatus Status,
    ExarotonPlayers? Players,
    ExarotonSoftware? Software,
    bool Shared)
{
    public bool IsOnline => Status == ExarotonStatus.Online;

    /// <summary>Encendido o en camino: no se puede volver a iniciar.</summary>
    public bool IsRunning => Status is not (ExarotonStatus.Offline or ExarotonStatus.Crashed);

    public string StatusText => Status switch
    {
        ExarotonStatus.Online => "En línea",
        ExarotonStatus.Offline => "Apagado",
        ExarotonStatus.Starting => "Iniciando…",
        ExarotonStatus.Stopping => "Deteniendo…",
        ExarotonStatus.Restarting => "Reiniciando…",
        ExarotonStatus.Saving => "Guardando…",
        ExarotonStatus.Loading => "Cargando…",
        ExarotonStatus.Crashed => "Se cerró con un error",
        ExarotonStatus.Pending => "En cola…",
        ExarotonStatus.Transferring => "Transfiriendo…",
        ExarotonStatus.Preparing => "Preparando…",
        _ => "Desconocido",
    };

    public string SoftwareText => Software is { Name: { Length: > 0 } name } s ? $"{name} {s.Version}".Trim() : "exaroton";
}

public sealed record ExarotonAccount(string Name, string? Email, int Credits);

/// <summary>Un error de la API de exaroton (token no válido, servidor sin permisos…), con su mensaje.</summary>
public sealed class ExarotonException(string message) : Exception(message);

/// <summary>
/// API v1 de exaroton (https://developers.exaroton.com): cuenta, servidores, encender/apagar/reiniciar, comandos,
/// registro y archivos. El token lo crea el jugador en los ajustes de su cuenta de exaroton y va en cada petición.
/// Todas las respuestas JSON llevan <c>{success, error, data}</c>.
/// </summary>
public sealed class ExarotonClient
{
    public const string HttpClientName = "exaroton";
    public const string BaseUrl = "https://api.exaroton.com/v1/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly string _token;

    public ExarotonClient(IHttpClientFactory httpFactory, string token)
    {
        _httpFactory = httpFactory;
        _token = token.Trim();
    }

    public Task<ExarotonAccount> GetAccountAsync(CancellationToken ct = default) => SendAsync<ExarotonAccount>(HttpMethod.Get, "account/", null, ct);

    public Task<List<ExarotonServer>> GetServersAsync(CancellationToken ct = default) => SendAsync<List<ExarotonServer>>(HttpMethod.Get, "servers/", null, ct);

    public Task<ExarotonServer> GetServerAsync(string id, CancellationToken ct = default) =>
        SendAsync<ExarotonServer>(HttpMethod.Get, $"servers/{Id(id)}", null, ct);

    public Task StartAsync(string id, CancellationToken ct = default) => SendAsync<JsonElement?>(HttpMethod.Get, $"servers/{Id(id)}/start/", null, ct);

    public Task StopAsync(string id, CancellationToken ct = default) => SendAsync<JsonElement?>(HttpMethod.Get, $"servers/{Id(id)}/stop/", null, ct);

    public Task RestartAsync(string id, CancellationToken ct = default) => SendAsync<JsonElement?>(HttpMethod.Get, $"servers/{Id(id)}/restart/", null, ct);

    /// <summary>Ejecuta un comando en la consola (sin la barra: <c>say Hola</c>).</summary>
    public Task CommandAsync(string id, string command, CancellationToken ct = default) =>
        SendAsync<JsonElement?>(HttpMethod.Post, $"servers/{Id(id)}/command/", new { command }, ct);

    /// <summary>Registro actual del servidor (el texto de su consola).</summary>
    public async Task<string> GetLogsAsync(string id, CancellationToken ct = default) =>
        (await SendAsync<LogData>(HttpMethod.Get, $"servers/{Id(id)}/logs/", null, ct).ConfigureAwait(false)).Content ?? string.Empty;

    /// <summary>Escribe (o crea) un archivo del servidor: <c>mods/sodium.jar</c>.</summary>
    public async Task UploadFileAsync(string id, string path, Stream content, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"servers/{Id(id)}/files/data/{FilePath(path)}/")
        {
            Content = new StreamContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        await ReadAsync<JsonElement?>(response, ct).ConfigureAwait(false);
    }

    private HttpClient Http
    {
        get
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            http.BaseAddress ??= new Uri(BaseUrl);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return http;
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lee <c>{success, error, data}</c>: con <c>success=false</c> (o un estado de error sin cuerpo) lanza
    /// <see cref="ExarotonException"/> con el mensaje de exaroton.
    /// </summary>
    internal static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        Envelope<T>? envelope = null;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(Json, ct).ConfigureAwait(false);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
        }

        if (envelope is { Success: true })
            return envelope.Data!;
        var message = envelope?.Error
                      ?? (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "El token de exaroton no es válido." : $"exaroton respondió {(int)response.StatusCode}.");
        throw new ExarotonException(message);
    }

    private static string Id(string id) => Uri.EscapeDataString(id);

    private static string FilePath(string path) =>
        string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private sealed record Envelope<T>(bool Success, string? Error, T? Data);

    private sealed record LogData(string? Content);
}
