using System.Text.Json;
using KonnorLauncher.Core.Modrinth;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Progress;
using KonnorLauncher.Core.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Instances;

/// <summary>
/// Resultado de sincronizar una instancia: la versión que publica el servidor. O bien
/// <paramref name="Game"/> (el manifest publica versión y cargador y el launcher los instala), o bien
/// <paramref name="VersionId"/> (el servidor envía la carpeta <c>versions</c> y se lanza la que hay).
/// </summary>
/// <param name="ServerUnreachable">El servidor no respondió (no es un fallo de los archivos).</param>
public sealed record InstanceSyncResult(bool Success, string Message, string? VersionId, bool ServerUnreachable = false,
    ManifestGame? Game = null)
{
    public static InstanceSyncResult Fail(string message, bool serverUnreachable = false) => new(false, message, null, serverUnreachable);
}

/// <summary>
/// Sincroniza una instancia con el servidor (LauncherBackEnd) y detecta qué versión hay que lanzar.
/// En una instancia sincronizada la versión la decide el servidor:
/// <list type="bullet">
/// <item>la de la sección <c>minecraft</c> del manifest: el launcher instala el juego y el cargador por
/// su cuenta, como en una instancia normal, y los mods que están en Modrinth se descargan de allí;</item>
/// <item>si el manifest no la trae (servidor antiguo o versión no reconocida), la que publica en
/// <c>versions/</c> (la del cargador si hay una, por ejemplo <c>1.20.1-forge-47.3.0</c>).</item>
/// </list>
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
    private readonly ModrinthClient? _modrinth;

    /// <param name="metaDir">Carpeta compartida (Java); por defecto la de los datos del launcher.</param>
    /// <param name="modrinth">Para descargar de Modrinth los archivos que publica con su hash; null = todo del servidor.</param>
    public InstanceSync(IHttpClientFactory httpFactory, ILoggerFactory? loggerFactory = null, string? metaDir = null,
        ModrinthClient? modrinth = null)
    {
        _httpFactory = httpFactory;
        _metaDir = metaDir;
        _modrinth = modrinth;
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

        string? announced = null;
        ManifestGame? game = null;
        VerificationResult? result = null;
        FetchedPolicy? fetched = null;
        // Dos intentos: si el servidor regeneró el manifest entre la política y la descarga, el hash no coincide.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // 1. Las reglas del servidor, firmadas: deciden qué manifest se acepta.
            fetched = await FetchPolicyAsync(instance, ct).ConfigureAwait(false);
            switch (fetched.Result)
            {
                case PolicyFetch.Unreachable:
                    return InstanceSyncResult.Fail("El servidor no responde. Comprueba la dirección y que esté en marcha.", serverUnreachable: true);
                case PolicyFetch.Rejected:
                    return InstanceSyncResult.Fail(fetched.Reason ?? "El servidor no se pudo identificar.");
                case PolicyFetch.NotSupported when instance.ServerKey is not null:
                    // Ya firmó antes: no se acepta que de repente deje de hacerlo (otro servidor en su dirección).
                    return InstanceSyncResult.Fail("El servidor ya no firma sus reglas: no es el servidor con el que se vinculó esta instancia.");
            }

            var legacy = fetched.Result == PolicyFetch.NotSupported;
            var verifier = new ClientVerifier(_httpFactory.CreateClient(HttpClientName), _loggerFactory.CreateLogger<ClientVerifier>())
            {
                // No cuenta como instalada hasta que los archivos se hayan descargado (ver más abajo).
                ClientVersionReceived = v => announced = v,
                ManifestReceived = m =>
                {
                    game = m.Game;
                    // Servidor que no firma (y con el que la instancia nunca se vinculó con firma): sus reglas, del
                    // manifest y solo mientras el launcher esté abierto (no se guardan).
                    if (legacy)
                        ApplyManifestPolicy(instance, m);
                },
                // Solo para descargar: lo comprobado sigue siendo el SHA-256 del manifest.
                MirrorLookup = _modrinth is null ? null : (hashes, token) => _modrinth.FindFilesBySha512Async(hashes, token),
            };
            result = await verifier.VerifyAsync(new VerifierOptions
            {
                GameDir = layout.GameDir,
                ServerUrl = instance.ServerUrl,
                EnableVerify = true,
                // Lo decide el servidor (delete_unauthorized en el manifest). Si un servidor antiguo no
                // lo publica, se limpia: una instancia sincronizada debe tener exactamente su contenido.
                DeleteUnauthorized = true,
                InstallPacks = instance.InstallPacks,
                ExpectedManifestSha256 = fetched.Policy?.ManifestSha256,
            }, progress, ct).ConfigureAwait(false);
            if (result.Status != VerificationException.ManifestMismatch)
                break;
            _logger.LogWarning("El manifest de {Id} no coincide con su política firmada (intento {Attempt})", instance.Id, attempt + 1);
        }

        if (!result!.Success)
            return result.Status switch
            {
                VerificationException.ServerNotResponding =>
                    InstanceSyncResult.Fail("El servidor no responde. Comprueba la dirección y que esté en marcha.", serverUnreachable: true),
                VerificationException.ManifestMismatch =>
                    InstanceSyncResult.Fail("La lista de archivos recibida no es la que firmó el servidor: no se instala nada. Inténtalo de nuevo."),
                _ => InstanceSyncResult.Fail("No se pudieron descargar los archivos del servidor."),
            };

        // Reglas firmadas: pasan a ser la «última sesión» de la instancia (fija la clave la primera vez).
        if (fetched is { Result: PolicyFetch.Ok, Signed: { } signed, Policy: { } policy })
            instance.AcceptSession(signed, policy, DateTimeOffset.Now);

        // Archivos al día: la versión anunciada pasa a ser la instalada.
        if (announced is not null)
            instance.ServerClientVersion = instance.AvailableClientVersion = announced;

        // Sincronizada con éxito: queda vinculada a su servidor (salvo que este la haya liberado).
        if (!instance.ServerReleased)
            instance.SyncLocked = true;

        if (game is not null)
        {
            _logger.LogInformation("Instancia {Id} sincronizada; el servidor pide {Minecraft} {Loader} {LoaderVersion}",
                instance.Id, game.MinecraftVersion, game.Loader, game.LoaderVersion);
            return new InstanceSyncResult(true, string.Empty, null, Game: game);
        }

        var version = DetectVersion(layout.VersionsDir);
        _logger.LogInformation("Instancia {Id} sincronizada; versión del servidor: {Version}", instance.Id, version ?? "(ninguna)");
        return version is null ? InstanceSyncResult.Fail(NoVersionText) : new InstanceSyncResult(true, string.Empty, version);
    }

    /// <summary>
    /// Adopta en la instancia la versión que publica el servidor tras sincronizar con éxito. Devuelve
    /// true si cambió (entonces la versión instalada se vuelve a resolver al jugar).
    /// </summary>
    public static bool AdoptServerVersion(GameInstance instance, InstanceSyncResult result)
    {
        var (minecraft, loader, loaderVersion) = result.Game is { } game
            ? (game.MinecraftVersion, game.Loader, game.LoaderVersion)
            // El id ya incluye el cargador (p. ej. 1.20.1-forge-47.3.0): se lanza tal cual.
            : (result.VersionId ?? string.Empty, ModLoader.Vanilla, (string?)null);
        if (instance.MinecraftVersion == minecraft && instance.Loader == loader && instance.LoaderVersion == loaderVersion)
            return false;

        instance.MinecraftVersion = minecraft;
        instance.Loader = loader;
        instance.LoaderVersion = loaderVersion;
        instance.LoaderChannel = loader == ModLoader.Vanilla ? LoaderChannel.Stable : LoaderChannel.Specific;
        instance.InstalledVersionId = null;
        return true;
    }

    /// <summary>
    /// Consulta rápida de las reglas del servidor (sin descargar archivos ni el manifest): la política firmada
    /// (<see cref="ServerPolicyCrypto.Endpoint"/>), que pasa a ser la última sesión de la instancia, con la versión que
    /// anuncia (<see cref="GameInstance.AvailableClientVersion"/>; la instalada no cambia). Así el editor sabe al
    /// momento si el servidor la liberó y la lista, si hay una actualización. Un servidor que no firma (y con el que
    /// la instancia nunca se vinculó con firma) se consulta como antes, por su manifest. false si el servidor no
    /// responde o no se identifica.
    /// </summary>
    public async Task<bool> RefreshPolicyAsync(GameInstance instance, CancellationToken ct = default)
    {
        var fetched = await FetchPolicyAsync(instance, ct).ConfigureAwait(false);
        switch (fetched.Result)
        {
            case PolicyFetch.Ok:
                instance.AcceptSession(fetched.Signed!, fetched.Policy!, DateTimeOffset.Now);
                return true;
            case PolicyFetch.NotSupported when instance.ServerKey is null:
                return await RefreshFromManifestAsync(instance, ct).ConfigureAwait(false);
            case PolicyFetch.Rejected:
                _logger.LogWarning("La política del servidor de {Id} se rechazó: {Reason}", instance.Id, fetched.Reason);
                return false;
            default:
                return false;
        }
    }

    /// <summary>Servidor que no firma: sus reglas, del manifest (solo para esta sesión del launcher).</summary>
    private async Task<bool> RefreshFromManifestAsync(GameInstance instance, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3)); // como la sincronización: sin respuesta en 3 s, no responde
            var url = instance.ServerUrl.Trim().TrimEnd('/') + "/client_manifest.json";
            var json = await _httpFactory.CreateClient(HttpClientName).GetStringAsync(url, timeout.Token).ConfigureAwait(false);
            var manifest = ManifestParser.Parse(json);
            if (!string.IsNullOrWhiteSpace(manifest.ClientVersion))
                instance.AvailableClientVersion = manifest.ClientVersion;
            ApplyManifestPolicy(instance, manifest);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or VerificationException or InvalidOperationException
                                       or UriFormatException)
        {
            _logger.LogInformation("No se pudo consultar la política del servidor de {Id}: {Message}", instance.Id, ex.Message);
            return false;
        }
    }

    /// <summary>Cómo terminó la consulta de la política firmada.</summary>
    private enum PolicyFetch
    {
        /// <summary>Firmada, de la clave fijada (o la primera) y vigente.</summary>
        Ok,

        /// <summary>El servidor no tiene el servicio (no firma: versión anterior o LauncherBackEnd).</summary>
        NotSupported,

        Unreachable,

        /// <summary>Respondió, pero la firma no vale, es de otra clave o ya caducó.</summary>
        Rejected,
    }

    private sealed record FetchedPolicy(PolicyFetch Result, SignedServerPolicy? Signed = null, ServerPolicy? Policy = null, string? Reason = null);

    /// <summary>Pide la política firmada (3 s como máximo) y la comprueba con la clave fijada en la instancia.</summary>
    private async Task<FetchedPolicy> FetchPolicyAsync(GameInstance instance, CancellationToken ct)
    {
        string json;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var url = instance.ServerUrl.Trim().TrimEnd('/') + ServerPolicyCrypto.Endpoint;
            using var response = await _httpFactory.CreateClient(HttpClientName).GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new FetchedPolicy(PolicyFetch.NotSupported);
            if (!response.IsSuccessStatusCode)
                return new FetchedPolicy(PolicyFetch.Unreachable);
            json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            _logger.LogInformation("No se pudo consultar la política del servidor de {Id}: {Message}", instance.Id, ex.Message);
            return new FetchedPolicy(PolicyFetch.Unreachable);
        }

        var signed = ServerPolicyCrypto.Deserialize(json);
        var check = ServerPolicyCrypto.Verify(signed, instance.ServerKey, DateTimeOffset.Now);
        return check switch
        {
            { Status: PolicyStatus.Valid, Policy: { } policy } => new FetchedPolicy(PolicyFetch.Ok, signed, policy),
            { Status: PolicyStatus.KeyMismatch } => new FetchedPolicy(PolicyFetch.Rejected, Reason:
                "La identidad del servidor no coincide con la de cuando se vinculó esta instancia: puede ser otro servidor en esa dirección."),
            { Status: PolicyStatus.Expired } => new FetchedPolicy(PolicyFetch.Rejected, Reason:
                "La respuesta del servidor está caducada: comprueba la fecha y la hora de este equipo."),
            _ => new FetchedPolicy(PolicyFetch.Rejected, Reason: "La respuesta del servidor no tiene una firma válida."),
        };
    }

    /// <summary>
    /// Al abrir el launcher: consulta las reglas de cada instancia sincronizada, a la vez, y guarda lo
    /// que haya cambiado (política del servidor y versión anunciada). No descarga ningún archivo.
    /// Devuelve las instancias actualizadas, tal como quedan en disco.
    /// </summary>
    public async Task<IReadOnlyList<GameInstance>> CheckLinkedInstancesAsync(InstanceStore store, CancellationToken ct = default)
    {
        var linked = store.LoadAll().Where(i => i.SyncWithServer && !string.IsNullOrWhiteSpace(i.ServerUrl)).ToList();
        var checks = await Task.WhenAll(linked.Select(async instance =>
        {
            var probe = instance.Clone();
            var changed = await RefreshPolicyAsync(probe, ct).ConfigureAwait(false) && PolicyDiffers(instance, probe);
            return (Probe: probe, Changed: changed);
        })).ConfigureAwait(false);

        var updated = new List<GameInstance>();
        foreach (var (probe, _) in checks.Where(c => c.Changed))
        {
            try
            {
                // Se relee justo antes de guardar y solo se copia la política: si mientras tanto se jugó
                // o se editó la instancia, no se pisa nada más.
                var stored = store.LoadAll().FirstOrDefault(i => i.Id == probe.Id);
                if (stored is null)
                    continue;
                CopyPolicy(probe, stored);
                store.Save(stored);
                updated.Add(stored);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(ex, "No se pudo guardar lo que anuncia el servidor de {Id}", probe.Id);
            }
        }

        _logger.LogInformation("Servidores consultados al abrir: {Linked} instancias, {Updated} con cambios", linked.Count, updated.Count);
        return updated;
    }

    /// <summary>
    /// Lo que el servidor decide sobre la instancia y se consulta sin descargar archivos: la sesión firmada (y la
    /// clave, la primera vez) y las reglas ya aplicadas.
    /// </summary>
    public static void CopyPolicy(GameInstance from, GameInstance to)
    {
        to.ServerKey = from.ServerKey;
        to.ServerSession = from.ServerSession;
        to.ServerReleaseProof = from.ServerReleaseProof;
        to.ServerAlwaysOnline = from.ServerAlwaysOnline;
        to.ServerLocksUrl = from.ServerLocksUrl;
        to.ServerReleased |= from.ServerReleased; // irreversible
        to.ServerProtectsPacks = from.ServerProtectsPacks;
        to.ServerAutoJoin = from.ServerAutoJoin;
        to.ServerMemoryMb = from.ServerMemoryMb;
        to.InstallPacks |= from.ServerProtectsPacks;
        to.AvailableClientVersion = from.AvailableClientVersion;
        if (to.ServerKey is not null)
            to.ApplySession(DateTimeOffset.Now);
    }

    /// <summary>
    /// Si hay algo que guardar: solo lo que se persiste. Las reglas aplicadas (ServerAlwaysOnline…) no se guardan
    /// —salen de la sesión firmada al cargar—, así que con un servidor que no firma diferirían en cada consulta
    /// sin que hubiera nada nuevo que escribir.
    /// </summary>
    public static bool PolicyDiffers(GameInstance a, GameInstance b) =>
        a.ServerKey != b.ServerKey
        || a.ServerSession?.Signature != b.ServerSession?.Signature
        || a.ServerReleaseProof?.Signature != b.ServerReleaseProof?.Signature
        || a.InstallPacks != b.InstallPacks
        || a.AvailableClientVersion != b.AvailableClientVersion;

    /// <summary>Si cambian las reglas aplicadas en memoria (para refrescar un formulario abierto).</summary>
    public static bool RulesDiffer(GameInstance a, GameInstance b) =>
        a.ServerAlwaysOnline != b.ServerAlwaysOnline
        || a.ServerLocksUrl != b.ServerLocksUrl
        || a.ServerReleased != b.ServerReleased
        || a.ServerProtectsPacks != b.ServerProtectsPacks
        || a.ServerAutoJoin != b.ServerAutoJoin
        || a.ServerMemoryMb != b.ServerMemoryMb;

    /// <summary>
    /// Reglas de un servidor que no firma, leídas de su manifest. <c>protect_packs</c>: si el servidor obliga a
    /// instalar sus packs, la casilla queda marcada (y sigue marcada si después deja de obligar).
    /// </summary>
    private static void ApplyManifestPolicy(GameInstance instance, ClientManifest manifest)
    {
        instance.ServerAlwaysOnline = manifest.AlwaysOnline ?? true;
        instance.ServerLocksUrl = manifest.LockServerUrl ?? true;
        instance.ServerAutoJoin = manifest.AutoJoinAddress;
        instance.ServerMemoryMb = manifest.RecommendedMemoryMb;
        instance.ServerProtectsPacks = manifest.ProtectPacks == true;
        if (instance.ServerProtectsPacks)
            instance.InstallPacks = true;
        if (manifest.Released == true)
            instance.ServerReleased = true;
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
