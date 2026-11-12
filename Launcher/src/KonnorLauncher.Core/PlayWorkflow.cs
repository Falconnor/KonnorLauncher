using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Launching;
using KonnorLauncher.Core.Loaders;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Players;
using KonnorLauncher.Core.Progress;
using KonnorLauncher.Core.Verification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core;

/// <summary>Resultado del flujo de JUGAR. <paramref name="Message"/> es el texto del estado ("" = sin mensaje).</summary>
public sealed record PlayResult(bool Success, string Message)
{
    public static PlayResult Ok() => new(true, string.Empty);
    public static PlayResult Fail(string message) => new(false, message);
}

/// <summary>
/// Orquesta todo lo que ocurre al pulsar JUGAR sobre una instancia:
/// <code>
/// verificar con el servidor (si la instancia se sincroniza) → instalar el cargador → instalar con
/// CmlLib (vanilla, librerías, assets, Java) → lanzar → esperar
/// </code>
/// La verificación va antes porque decide la versión: la que publica el manifest (y entonces el
/// cargador se instala aquí, como en una instancia normal) o, con servidores que envían la carpeta
/// <c>versions</c>, la que ya dejó en disco. La UI pide el nombre de jugador antes de llamar a <see cref="RunAsync"/>.
/// </summary>
public sealed class PlayWorkflow
{
    /// <summary>Nombre del HttpClient del servidor propio.</summary>
    public const string ServerHttpClientName = InstanceSync.HttpClientName;

    // Textos que ve el jugador.
    public const string VerifyFailedText = "Error de conexión / Verificación fallida";
    public const string LaunchFailedText = "Error iniciando el juego";
    public const string NoVersionText = "Error al iniciar el juego: la instancia no tiene versión de Minecraft";

    private readonly LauncherSettings _settings;
    private readonly InstanceSync _sync;
    private readonly GameLauncher _gameLauncher;
    private readonly LoaderInstaller _loaderInstaller;
    private readonly InstanceStore _instances;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public PlayWorkflow(LauncherSettings settings, InstanceSync sync, GameLauncher gameLauncher,
        LoaderInstaller loaderInstaller, InstanceStore instances, ILoggerFactory? loggerFactory = null)
    {
        _settings = settings;
        _sync = sync;
        _gameLauncher = gameLauncher;
        _loaderInstaller = loaderInstaller;
        _instances = instances;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<PlayWorkflow>();
    }

    /// <summary>
    /// Ejecuta el flujo completo para <paramref name="instance"/> y espera a que el juego se cierre.
    /// Al terminar guarda en la instancia la versión lanzada y la fecha de la partida. Con
    /// <paramref name="joinServer"/>, el juego entra directamente a ese servidor.
    /// </summary>
    public async Task<PlayResult> RunAsync(GameInstance instance, IProgress<LauncherProgress>? progress, CancellationToken ct = default,
        Servers.ServerAddress? joinServer = null)
    {
        // launcher.properties (nombre, RAM, Java) se relee en cada partida por si se editó a mano.
        _settings.Properties.Reload();

        // En una instancia sincronizada la versión la decide el servidor (se comprueba tras sincronizar).
        if (!instance.SyncWithServer && string.IsNullOrWhiteSpace(instance.MinecraftVersion))
            return PlayResult.Fail(NoVersionText);
        if (!UsernameRules.IsValid(_settings.PlayerUsername))
            return PlayResult.Fail("Error al iniciar el juego: nombre de jugador no válido");

        var layout = InstanceLayout.For(instance, _instances.InstancesDir, LauncherPaths.MetaDir);
        layout.EnsureCreated();

        // ── 1. Verificación con el servidor (solo instancias sincronizadas) ──
        if (instance.SyncWithServer)
        {
            progress?.Report(new LauncherProgress(LauncherPhase.Verifying, string.Empty));
            try
            {
                var result = await _sync.SyncAsync(instance, _instances.InstancesDir, progress, ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    // "Siempre en línea" desactivado en el servidor: si no responde, se juega con lo que hay.
                    var canPlayOffline = result.ServerUnreachable && !instance.ServerAlwaysOnline
                                         && !string.IsNullOrWhiteSpace(instance.MinecraftVersion);
                    if (!canPlayOffline)
                        return PlayResult.Fail(result.Message == InstanceSync.NoVersionText ? result.Message : VerifyFailedText);
                    _logger.LogWarning("El servidor no responde y no exige conexión: se juega con los archivos actuales");
                    progress?.Report(new LauncherProgress(LauncherPhase.Verifying, "El servidor no responde: se juega con los archivos actuales"));
                }
                // Si el servidor cambió de versión, la instancia la adopta.
                else
                {
                    var old = instance.Summary;
                    if (InstanceSync.AdoptServerVersion(instance, result))
                        _logger.LogInformation("La versión del servidor cambió: {Old} → {New}", old, instance.Summary);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error de verificación");
                return PlayResult.Fail($"Error de verificacion: {ex.Message}");
            }
        }

        // auto_join del servidor: si no se pidió otro servidor, el juego entra directamente al suyo.
        if (joinServer is null && instance.SyncWithServer && Servers.ServerAddress.TryParse(instance.ServerAutoJoin, out var autoJoin))
        {
            joinServer = autoJoin;
            _logger.LogInformation("El servidor de la instancia pide entrar directamente a {Address}", autoJoin);
        }

        var request = new LaunchRequest
        {
            GameDir = layout.GameDir,
            Layout = layout,
            VersionId = instance.MinecraftVersion,
            Username = _settings.PlayerUsername.Trim(),
            MemoryMb = instance.ResolveMemoryMb(_settings.MemoryMb), // la de la instancia o la que recomienda su servidor
            JavaPathSetting = _settings.JavaPath,
            Uuid = _settings.PlayerUuid,
            AccessToken = _settings.PlayerAccessToken,
            JoinServer = joinServer,
            JvmArguments = _settings.JavaArgs,
            ScreenWidth = _settings.GameWidth is { } w && _settings.GameHeight is not null ? w : null,
            ScreenHeight = _settings.GameWidth is not null ? _settings.GameHeight : null,
            FullScreen = _settings.GameFullscreen,
        };
        var path = layout.ToMinecraftPath();

        // ── 2. Cargador de mods y archivos del juego ─────────────────────
        bool online;
        try
        {
            var versionId = await InstallLoaderAsync(instance, path, progress, ct).ConfigureAwait(false);
            request = request with { VersionId = versionId };
            online = await _gameLauncher.InstallAsync(request, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error instalando {Instance}", instance.Summary);
            return PlayResult.Fail($"Error de instalación: {ex.Message}");
        }

        instance.InstalledVersionId = request.VersionId;
        instance.LastPlayed = DateTimeOffset.Now;
        TrySave(instance);

        // ── 3. Lanzamiento (bloquea hasta que el juego se cierra) ───────
        int exitCode;
        try
        {
            exitCode = await _gameLauncher.LaunchAndWaitAsync(request, offline: !online, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al iniciar el juego");
            return PlayResult.Fail($"Error al iniciar el juego: {ex.Message}");
        }

        return exitCode == 0 ? PlayResult.Ok() : PlayResult.Fail(LaunchFailedText);
    }

    /// <summary>
    /// Instala el cargador y devuelve la versión que hay que lanzar. Sin conexión, si la última
    /// versión lanzada sigue en disco, se usa esa: así se puede jugar sin internet.
    /// </summary>
    private async Task<string> InstallLoaderAsync(GameInstance instance, CmlLib.Core.MinecraftPath path,
        IProgress<LauncherProgress>? progress, CancellationToken ct)
    {
        if (instance.Loader == ModLoader.Vanilla)
            return instance.MinecraftVersion;

        // Ya instalado con esta misma versión del cargador: no hace falta conexión.
        if (instance.InstalledVersionId is { } installed && GameLauncher.IsInstalledLocally(path, installed)
            && instance.Loader is ModLoader.Fabric or ModLoader.Quilt
            && installed.Contains(instance.LoaderVersion ?? "\0", StringComparison.Ordinal))
            return installed;

        try
        {
            var launcher = _gameLauncher.CreateLauncher(path, localOnly: false);
            return await _loaderInstaller.InstallAsync(instance, launcher, path, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   && instance.InstalledVersionId is { } offline && GameLauncher.IsInstalledLocally(path, offline))
        {
            _logger.LogWarning(ex, "Sin conexión; se usa el cargador ya instalado {Id}", offline);
            return offline;
        }
    }

    private void TrySave(GameInstance instance)
    {
        try
        {
            _instances.Save(instance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "No se pudo guardar la instancia {Id}", instance.Id);
        }
    }
}
