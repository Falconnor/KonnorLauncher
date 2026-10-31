using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Players;
using KonnorLauncher.Core.Progress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Launching;

/// <summary>Todo lo necesario para instalar y lanzar una versión.</summary>
public sealed record LaunchRequest
{
    public required string GameDir { get; init; }
    public required string VersionId { get; init; }
    public required string Username { get; init; }
    public int MemoryMb { get; init; } = 4096;

    /// <summary>Valor crudo de <c>java.path</c> (ver <see cref="JavaSelector"/>).</summary>
    public string? JavaPathSetting { get; init; }

    /// <summary><c>player.uuid</c> manual; si es null se calcula el UUID offline del nombre.</summary>
    public string? Uuid { get; init; }

    /// <summary><c>player.accessToken</c> manual; si es null se usa "0".</summary>
    public string? AccessToken { get; init; }

    public string VersionsPath { get; init; } = "versions";
    public string LibrariesPath { get; init; } = "libraries";
    public string AssetsPath { get; init; } = "assets";
}

/// <summary>
/// Instalación y lanzamiento del juego con CmlLib.Core, que resuelve las versiones (incluida la
/// herencia con <c>inheritsFrom</c>), las librerías, los natives, los assets y el Java de Mojang de
/// cada versión, y solo descarga lo que falta o está corrupto.
/// </summary>
public sealed class GameLauncher
{
    /// <summary>Nombre del HttpClient usado por CmlLib.</summary>
    public const string HttpClientName = "mojang";

    /// <summary>Texto que se muestra mientras se descarga el juego.</summary>
    public const string DownloadingText = "Descargando contenido...";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;

    public GameLauncher(IHttpClientFactory httpFactory, ILogger<GameLauncher>? logger = null)
    {
        _httpFactory = httpFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Nombre y versión que se pasan al juego (<c>${launcher_name}</c>).</summary>
    public string LauncherName { get; init; } = "KonnorLauncher";
    public string LauncherVersion { get; init; } = typeof(GameLauncher).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Ruta del log del juego (stdout/stderr). null = no se guarda.</summary>
    public string? GameLogPath { get; init; } = Path.Combine(LauncherPaths.LogsDir, "game-latest.log");

    // ── Rutas de CmlLib ──────────────────────────────────────────────────

    /// <summary>
    /// <see cref="MinecraftPath"/> con <c>versions.path</c>, <c>libraries.path</c> y <c>assets.path</c>
    /// mapeados (por defecto coinciden con la estructura estándar).
    /// </summary>
    public static MinecraftPath CreateMinecraftPath(LaunchRequest request)
    {
        var path = new MinecraftPath(request.GameDir)
        {
            Versions = LauncherPaths.ResolveGameSubDir(request.GameDir, request.VersionsPath, "versions"),
            Library = LauncherPaths.ResolveGameSubDir(request.GameDir, request.LibrariesPath, "libraries"),
            Assets = LauncherPaths.ResolveGameSubDir(request.GameDir, request.AssetsPath, "assets"),
        };
        return path;
    }

    private MinecraftLauncher CreateLauncher(MinecraftPath path, bool localOnly)
    {
        var parameters = MinecraftLauncherParameters.CreateDefault(path, _httpFactory.CreateClient(HttpClientName));
        if (localOnly)
            parameters.VersionLoader = new LocalJsonVersionLoader(path); // sin conexión: solo JSON locales
        return new MinecraftLauncher(parameters);
    }

    // ── Instalación ──────────────────────────────────────────────────────

    /// <summary>
    /// Instala o repara la versión (vanilla, herencia, librerías, assets y Java).
    /// Devuelve false si no hay conexión pero la versión ya está instalada: así se puede jugar
    /// sin internet con lo que ya hay en disco.
    /// </summary>
    public async Task<bool> InstallAsync(LaunchRequest request, IProgress<LauncherProgress>? progress, CancellationToken ct = default)
    {
        var path = CreateMinecraftPath(request);
        var launcher = CreateLauncher(path, localOnly: false);
        progress?.Report(new LauncherProgress(LauncherPhase.Installing, DownloadingText, 0));

        // ByteProgress da un porcentaje suave; de FileProgress solo se guarda el resumen en el log.
        var lastFile = default(InstallerProgressChangedEventArgs);
        var fileProgress = new ActionProgress<InstallerProgressChangedEventArgs>(e => lastFile = e);
        var byteProgress = new ActionProgress<ByteProgress>(b =>
        {
            var percent = b.TotalBytes > 0 ? Math.Clamp(b.ToRatio() * 100, 0, 100) : 0;
            progress?.Report(new LauncherProgress(LauncherPhase.Installing, DownloadingText, percent));
        });

        try
        {
            await launcher.InstallAsync(request.VersionId, fileProgress, byteProgress, ct).ConfigureAwait(false);
            _logger.LogInformation("Versión {Version} instalada/verificada ({Done}/{Total} tareas)",
                request.VersionId, lastFile?.ProgressedTasks, lastFile?.TotalTasks);
            return true;
        }
        catch (Exception ex) when (IsNetworkError(ex) && IsInstalledLocally(path, request.VersionId))
        {
            _logger.LogWarning(ex, "Sin conexión con Mojang; se lanzará {Version} con los archivos locales", request.VersionId);
            return false;
        }
    }

    // ── Lanzamiento ──────────────────────────────────────────────────────

    /// <summary>
    /// Construye el proceso, lo inicia y espera a que el juego se cierre. Devuelve el código de salida.
    /// Por seguridad no se registra el token ni la línea de comandos completa en los logs.
    /// </summary>
    public async Task<int> LaunchAndWaitAsync(LaunchRequest request, bool offline, IProgress<LauncherProgress>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.VersionId))
            throw new InvalidOperationException("No hay ninguna versión seleccionada");

        var path = CreateMinecraftPath(request);
        var launcher = CreateLauncher(path, localOnly: offline);
        progress?.Report(new LauncherProgress(LauncherPhase.Launching, string.Empty));

        var java = JavaSelector.Resolve(request.JavaPathSetting, request.GameDir);
        if (java.ConfiguredPathMissing)
            _logger.LogWarning("java.path no existe ({Configured}); se usa el Java de Mojang", request.JavaPathSetting);

        var option = new MLaunchOption
        {
            Session = CreateSession(request),
            MaximumRamMb = Math.Max(512, request.MemoryMb),
            GameLauncherName = LauncherName,
            GameLauncherVersion = LauncherVersion,
        };
        if (java.OverridePath is not null)
            option.JavaPath = java.OverridePath;

        var process = await launcher.BuildProcessAsync(request.VersionId, option, ct).ConfigureAwait(false);
        process.StartInfo.WorkingDirectory = request.GameDir; // el juego corre dentro de su carpeta
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;               // sin consola de Java

        _logger.LogInformation("Iniciando Minecraft {Version} | Java: {Java} | RAM: {Ram} MB | Usuario: {User}",
            request.VersionId, process.StartInfo.FileName, option.MaximumRamMb, request.Username);

        return await RunProcessAsync(process, progress).ConfigureAwait(false);
    }

    /// <summary>Inicia el proceso, guarda su salida en el log del juego y espera a que termine.</summary>
    private async Task<int> RunProcessAsync(Process process, IProgress<LauncherProgress>? progress)
    {
        StreamWriter? gameLog = null;
        try
        {
            if (GameLogPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GameLogPath)!);
                gameLog = new StreamWriter(GameLogPath, append: false) { AutoFlush = true };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo abrir el log del juego");
        }

        var wrapper = new ProcessWrapper(process);
        var sync = new object();
        wrapper.OutputReceived += (_, line) =>
        {
            if (gameLog is null)
                return;
            lock (sync)
                gameLog.WriteLine(line);
        };

        try
        {
            wrapper.StartWithEvents();
            progress?.Report(new LauncherProgress(LauncherPhase.Running, string.Empty));
            var exitCode = await wrapper.WaitForExitTaskAsync().ConfigureAwait(false);
            _logger.LogInformation("Minecraft terminó con código {ExitCode}", exitCode);
            return exitCode;
        }
        finally
        {
            lock (sync)
            {
                gameLog?.Dispose();
                gameLog = null;
            }

            process.Dispose();
        }
    }

    // ── Sesión offline ───────────────────────────────────────────────────

    /// <summary>
    /// Sesión offline: usa <c>player.uuid</c>/<c>player.accessToken</c> si existen; si no, el UUID
    /// offline del nombre (el mismo que calcula el servidor) y el token "0".
    /// </summary>
    public static MSession CreateSession(LaunchRequest request) =>
        new(request.Username, request.AccessToken ?? "0", request.Uuid ?? OfflineUuid.FromUsername(request.Username))
        {
            UserType = "msa", // el mismo que usa MSession.CreateOfflineSession de CmlLib
        };

    // ── Utilidades ───────────────────────────────────────────────────────

    /// <summary>True si existe <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>.</summary>
    public static bool IsInstalledLocally(MinecraftPath path, string versionId) =>
        File.Exists(path.GetVersionJsonPath(versionId));

    private static bool IsNetworkError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or System.Net.Sockets.SocketException or TimeoutException)
                return true;
            if (current is TaskCanceledException { InnerException: TimeoutException })
                return true;
        }

        return false;
    }

    /// <summary>
    /// IProgress que invoca la acción en el mismo hilo, sin capturar el contexto de la UI. Es necesario
    /// porque CmlLib emite miles de eventos y <see cref="Progress{T}"/> los encolaría todos en el dispatcher.
    /// </summary>
    private sealed class ActionProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
