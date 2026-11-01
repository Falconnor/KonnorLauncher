using System.Text.Json;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Progress;
using KonnorLauncher.Core.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Instances;

/// <summary>Resultado de sincronizar una instancia: la versión que publica el servidor, si se pudo detectar.</summary>
/// <param name="ServerUnreachable">El servidor no respondió (no es un fallo de los archivos).</param>
public sealed record InstanceSyncResult(bool Success, string Message, string? VersionId, bool ServerUnreachable = false)
{
    public static InstanceSyncResult Fail(string message, bool serverUnreachable = false) => new(false, message, null, serverUnreachable);
}

/// <summary>
/// Sincroniza una instancia con el servidor (LauncherBackEnd) y detecta qué versión hay que lanzar.
/// En una instancia sincronizada la versión la decide el servidor: es la que publica en
/// <c>versions/</c> (la del cargador si hay una, por ejemplo <c>1.20.1-forge-47.3.0</c>).
/// Lo usan el editor (al crear la instancia) y el flujo de JUGAR (antes de cada partida).
/// </summary>
public sealed class InstanceSync
{
    /// <summary>Nombre del HttpClient del servidor propio.</summary>
    public const string HttpClientName = "server";

    public const string NoVersionText =
        "El servidor no publica ninguna versión de Minecraft (su carpeta versions está vacía).";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly string? _metaDir;

    /// <param name="metaDir">Carpeta compartida (Java); por defecto la de los datos del launcher.</param>
    public InstanceSync(IHttpClientFactory httpFactory, ILoggerFactory? loggerFactory = null, string? metaDir = null)
    {
        _httpFactory = httpFactory;
        _metaDir = metaDir;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<InstanceSync>();
    }

    /// <summary>
    /// Descarga y verifica los archivos del servidor en la carpeta de la instancia y devuelve la
    /// versión publicada. Guarda en la instancia la <c>client_version</c> que anuncia el servidor.
    /// </summary>
    public async Task<InstanceSyncResult> SyncAsync(GameInstance instance, string instancesDir,
        IProgress<LauncherProgress>? progress, CancellationToken ct = default)
    {
        var layout = InstanceLayout.For(instance, instancesDir, _metaDir ?? LauncherPaths.MetaDir);
        layout.EnsureCreated();

        var verifier = new ClientVerifier(_httpFactory.CreateClient(HttpClientName), _loggerFactory.CreateLogger<ClientVerifier>())
        {
            ClientVersionReceived = v => instance.ServerClientVersion = v,
            // Política del servidor: se recuerda en la instancia para cuando no responda.
            ManifestReceived = m =>
            {
                instance.ServerAlwaysOnline = m.AlwaysOnline ?? true;
                if (m.Released == true)
                    instance.ServerReleased = true; // irreversible: un manifest posterior no la revierte
            },
        };
        var result = await verifier.VerifyAsync(new VerifierOptions
        {
            GameDir = layout.GameDir,
            ServerUrl = instance.ServerUrl,
            EnableVerify = true,
            // Lo decide el servidor (delete_unauthorized en el manifest). Si un servidor antiguo no
            // lo publica, se limpia: una instancia sincronizada debe tener exactamente su contenido.
            DeleteUnauthorized = true,
        }, progress, ct).ConfigureAwait(false);

        if (!result.Success)
            return result.Status == VerificationException.ServerNotResponding
                ? InstanceSyncResult.Fail("El servidor no responde. Comprueba la dirección y que esté en marcha.", serverUnreachable: true)
                : InstanceSyncResult.Fail("No se pudieron descargar los archivos del servidor.");

        // Sincronizada con éxito: queda vinculada a su servidor (salvo que este la haya liberado).
        if (!instance.ServerReleased)
            instance.SyncLocked = true;

        var version = DetectVersion(layout.VersionsDir);
        _logger.LogInformation("Instancia {Id} sincronizada; versión del servidor: {Version}", instance.Id, version ?? "(ninguna)");
        return version is null ? InstanceSyncResult.Fail(NoVersionText) : new InstanceSyncResult(true, string.Empty, version);
    }

    /// <summary>
    /// Consulta rápida de la política del servidor (sin descargar archivos) y la aplica a la instancia:
    /// así el editor sabe al momento si el servidor la liberó. false si el servidor no responde.
    /// </summary>
    public async Task<bool> RefreshPolicyAsync(GameInstance instance, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var url = instance.ServerUrl.Trim().TrimEnd('/') + "/client_manifest.json";
            var json = await _httpFactory.CreateClient(HttpClientName).GetStringAsync(url, timeout.Token).ConfigureAwait(false);
            var manifest = ManifestParser.Parse(json);
            instance.ServerAlwaysOnline = manifest.AlwaysOnline ?? true;
            if (manifest.Released == true)
                instance.ServerReleased = true;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or VerificationException or InvalidOperationException)
        {
            _logger.LogInformation("No se pudo consultar la política del servidor de {Id}: {Message}", instance.Id, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Versión que hay que lanzar entre las que hay en <paramref name="versionsDir"/>:
    /// <list type="number">
    /// <item>la de un cargador: su JSON hereda de otra versión (<c>inheritsFrom</c>) o, si es un JSON
    /// completo como los que generan algunos launchers, arranca con una clase que no es la de vanilla;</item>
    /// <item>si no hay ninguna, la versión vanilla;</item>
    /// <item>si hay varias del mismo tipo, la más reciente (<c>releaseTime</c>).</item>
    /// </list>
    /// null si no hay ninguna.
    /// </summary>
    /// <summary>Clases con las que arranca Minecraft sin cargador (moderna y antigua).</summary>
    private static readonly HashSet<string?> VanillaMainClasses = ["net.minecraft.client.main.Main", "net.minecraft.client.Minecraft"];

    public static string? DetectVersion(string versionsDir)
    {
        if (!Directory.Exists(versionsDir))
            return null;

        var candidates = new List<(string Id, bool IsLoader, DateTimeOffset Released)>();
        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            var json = Path.Combine(dir, id + ".json");
            if (!File.Exists(json))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var root = doc.RootElement;
                var isLoader = (root.TryGetProperty("inheritsFrom", out var parent) && parent.ValueKind == JsonValueKind.String)
                               || (root.TryGetProperty("mainClass", out var main) && main.ValueKind == JsonValueKind.String
                                   && !VanillaMainClasses.Contains(main.GetString()));
                var released = root.TryGetProperty("releaseTime", out var time) && time.ValueKind == JsonValueKind.String
                                                                                 && DateTimeOffset.TryParse(time.GetString(), out var parsed)
                    ? parsed
                    : File.GetLastWriteTimeUtc(json);
                candidates.Add((id, isLoader, released));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Una versión dañada no se elige.
            }
        }

        return candidates
            .OrderByDescending(c => c.IsLoader)
            .ThenByDescending(c => c.Released)
            .Select(c => c.Id)
            .FirstOrDefault();
    }
}
