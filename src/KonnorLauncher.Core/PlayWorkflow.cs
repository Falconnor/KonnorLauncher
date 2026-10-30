using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Launching;
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
/// Orquesta todo lo que ocurre al pulsar JUGAR:
/// <code>
/// versión seleccionada? → verificar (si enable.verify) → instalar/resolver con CmlLib → lanzar → esperar
/// </code>
/// La verificación va ANTES de CmlLib para que los JSON de las versiones personalizadas que publica
/// el servidor (Forge, Fabric...) ya estén en disco, y se hace siempre, esté o no instalada la versión.
/// La UI pide el nombre de jugador antes de llamar a <see cref="RunAsync"/>.
/// </summary>
public sealed class PlayWorkflow
{
    /// <summary>Nombre del HttpClient del servidor propio.</summary>
    public const string ServerHttpClientName = "server";

    // Textos que ve el jugador (los mismos que en la versión anterior).
    public const string VerifyFailedText = "Error de conexión / Verificación fallida";
    public const string LaunchFailedText = "Error iniciando el juego";
    public const string NoVersionText = "Error al iniciar el juego: No hay ninguna versión seleccionada";

    private readonly LauncherSettings _settings;
    private readonly LauncherPaths _paths;
    private readonly IHttpClientFactory _httpFactory;
    private readonly GameLauncher _gameLauncher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public PlayWorkflow(LauncherSettings settings, LauncherPaths paths, IHttpClientFactory httpFactory, GameLauncher gameLauncher, ILoggerFactory? loggerFactory = null)
    {
        _settings = settings;
        _paths = paths;
        _httpFactory = httpFactory;
        _gameLauncher = gameLauncher;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<PlayWorkflow>();
    }

    /// <summary>
    /// Ejecuta el flujo completo y espera a que el juego se cierre.
    /// Las fases se informan por <paramref name="progress"/> (la UI cambia el texto del botón con ellas).
    /// </summary>
    public async Task<PlayResult> RunAsync(IProgress<LauncherProgress>? progress, CancellationToken ct = default)
    {
        // launcher.properties se relee en cada partida por si se editó a mano.
        _settings.Properties.Reload();

        var version = _settings.MinecraftVersion;
        if (string.IsNullOrWhiteSpace(version))
            return PlayResult.Fail(NoVersionText);
        if (!UsernameRules.IsValid(_settings.PlayerUsername))
            return PlayResult.Fail("Error al iniciar el juego: nombre de jugador no válido");

        // ── 1. Verificación con el servidor propio ──────────────────────
        progress?.Report(new LauncherProgress(LauncherPhase.Verifying, string.Empty));
        string gameDir;
        try
        {
            gameDir = _paths.EnsureGameDir(_settings.GamePath);
            var verifier = new ClientVerifier(_httpFactory.CreateClient(ServerHttpClientName), _loggerFactory.CreateLogger<ClientVerifier>())
            {
                // Guarda la versión del cliente que anuncia el servidor (cached.client.version).
                ClientVersionReceived = v =>
                {
                    if (v != _settings.CachedClientVersion)
                        _settings.Properties.Set(LauncherSettings.KeyCachedClientVersion, v);
                },
            };
            var result = await verifier.VerifyAsync(new VerifierOptions
            {
                GameDir = gameDir,
                ServerUrl = _settings.ServerUrl,
                EnableVerify = _settings.EnableVerify,
                DeleteUnauthorized = _settings.DeleteUnauthorized,
            }, progress, ct).ConfigureAwait(false);

            if (!result.Success)
                return PlayResult.Fail(VerifyFailedText);
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

        var request = new LaunchRequest
        {
            GameDir = gameDir,
            VersionId = version,
            Username = _settings.PlayerUsername.Trim(),
            MemoryMb = _settings.MemoryMb,
            JavaPathSetting = _settings.JavaPath,
            Uuid = _settings.PlayerUuid,
            AccessToken = _settings.PlayerAccessToken,
            VersionsPath = _settings.VersionsPath,
            LibrariesPath = _settings.LibrariesPath,
            AssetsPath = _settings.AssetsPath,
        };

        // ── 2. Instalación / resolución con CmlLib (vanilla, herencia, Java) ─
        // Si falla, se muestra el mismo texto de error que en la verificación.
        bool online;
        try
        {
            online = await _gameLauncher.InstallAsync(request, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error instalando {Version}", version);
            return PlayResult.Fail($"Error de verificacion: {ex.Message}");
        }

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
}
