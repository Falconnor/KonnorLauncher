using System.Text.Json;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Progress;
using KonnorLauncher.Core.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Instances;

/// <summary>Resultado de sincronizar una instancia: la versión que publica el servidor, si se pudo detectar.</summary>
public sealed record InstanceSyncResult(bool Success, string Message, string? VersionId)
{
    public static InstanceSyncResult Fail(string message) => new(false, message, null);
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

    public InstanceSync(IHttpClientFactory httpFactory, ILoggerFactory? loggerFactory = null)
    {
        _httpFactory = httpFactory;
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
        var layout = InstanceLayout.For(instance, instancesDir, LauncherPaths.MetaDir);
        layout.EnsureCreated();

        var verifier = new ClientVerifier(_httpFactory.CreateClient(HttpClientName), _loggerFactory.CreateLogger<ClientVerifier>())
        {
            ClientVersionReceived = v => instance.ServerClientVersion = v,
        };
        var result = await verifier.VerifyAsync(new VerifierOptions
        {
            GameDir = layout.GameDir,
            ServerUrl = instance.ServerUrl,
            EnableVerify = true,
            DeleteUnauthorized = instance.DeleteUnauthorized,
        }, progress, ct).ConfigureAwait(false);

        if (!result.Success)
            return InstanceSyncResult.Fail(result.Status == VerificationException.ServerNotResponding
                ? "El servidor no responde. Comprueba la dirección y que esté en marcha."
                : "No se pudieron descargar los archivos del servidor.");

        var version = DetectVersion(layout.VersionsDir);
        _logger.LogInformation("Instancia {Id} sincronizada; versión del servidor: {Version}", instance.Id, version ?? "(ninguna)");
        return version is null ? InstanceSyncResult.Fail(NoVersionText) : new InstanceSyncResult(true, string.Empty, version);
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
