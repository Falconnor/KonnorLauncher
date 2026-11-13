namespace KonnorLauncher.Core.Paths;

/// <summary>
/// Resolución de todas las rutas: la carpeta del launcher, la del juego y sus subcarpetas.
/// Están centralizadas para que el verificador y el lanzador usen siempre las mismas.
/// </summary>
public sealed class LauncherPaths
{
    /// <summary>Nombre del archivo de configuración junto al ejecutable.</summary>
    public const string PropertiesFileName = "launcher.properties";

    /// <param name="launchDir">Carpeta del ejecutable.</param>
    /// <param name="dataDir">Carpeta de datos (null = <see cref="DataDir"/>); la cambian las pruebas.</param>
    public LauncherPaths(string launchDir, string? dataDir = null)
    {
        LaunchDir = Path.GetFullPath(launchDir);
        _dataDir = dataDir;
    }

    private readonly string? _dataDir;

    /// <summary>
    /// Carpeta del ejecutable (<c>LAUNCH_PATH</c>). En una publicación single-file,
    /// <see cref="AppContext.BaseDirectory"/> apunta a la carpeta del .exe, no a la de extracción.
    /// </summary>
    public string LaunchDir { get; }

    /// <summary>
    /// Ruta de <c>launcher.properties</c>: junto al ejecutable; instalado con Velopack, en la carpeta de datos, porque
    /// cada actualización reemplaza entera la carpeta del programa (<c>current</c>) y se perdería la configuración.
    /// En las instalaciones antiguas (Inno Setup) la carpeta del programa era la de datos: es el mismo archivo.
    /// </summary>
    public string PropertiesPath => IsVelopackInstall(LaunchDir)
        ? Path.Combine(_dataDir ?? DataDir, PropertiesFileName)
        : Path.Combine(LaunchDir, PropertiesFileName);

    /// <summary>Velopack instala en <c>&lt;raíz&gt;\current</c>, con <c>Update.exe</c> en la raíz.</summary>
    public static bool IsVelopackInstall(string launchDir)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(launchDir));
        return string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase)
               && Path.GetDirectoryName(dir) is { } root
               && File.Exists(Path.Combine(root, "Update.exe"));
    }

    /// <summary>
    /// Si la configuración va en la carpeta de datos y aún no existe, la crea con la que trae el programa (la
    /// configuración por defecto). Nunca pisa una existente.
    /// </summary>
    public void EnsurePropertiesFile()
    {
        var target = PropertiesPath;
        var bundled = Path.Combine(LaunchDir, PropertiesFileName);
        if (File.Exists(target) || string.Equals(target, bundled, StringComparison.OrdinalIgnoreCase) || !File.Exists(bundled))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(bundled, target, overwrite: false);
    }

    /// <summary>Crea las rutas a partir de la carpeta del ejecutable actual.</summary>
    public static LauncherPaths FromCurrentProcess() => new(AppContext.BaseDirectory);

    /// <summary>Carpeta de datos del launcher en %APPDATA% (logs). Es la carpeta de instalación.</summary>
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KonnorLauncher");

    /// <summary>Carpeta de logs: %APPDATA%\KonnorLauncher\logs.</summary>
    public static string LogsDir => Path.Combine(AppDataDir, "logs");

    /// <summary>
    /// Carpeta de datos del juego: %APPDATA%\KonnorLauncher, o la de la variable <c>KONNOR_DATA_DIR</c>
    /// (para pruebas o una instalación portátil).
    /// </summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("KONNOR_DATA_DIR") is { Length: > 0 } custom ? Path.GetFullPath(custom) : AppDataDir;

    /// <summary>
    /// Caché que se puede borrar sin perder nada (iconos de Modrinth, la primera página de Explorar):
    /// %LOCALAPPDATA%\KonnorLauncher\cache, o dentro de <c>KONNOR_DATA_DIR</c> si está definida.
    /// </summary>
    public static string CacheDir =>
        Environment.GetEnvironmentVariable("KONNOR_DATA_DIR") is { Length: > 0 } custom
            ? Path.Combine(Path.GetFullPath(custom), "cache")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KonnorLauncher", "cache");

    /// <summary>Cuenta del jugador: su skin y su foto de perfil (copias, por si se borra el original).</summary>
    public static string PlayerDir => Path.Combine(DataDir, "player");

    /// <summary>Instancias: una carpeta por instancia con su <c>instance.json</c>, mods, config, saves...</summary>
    public static string InstancesDir => Path.Combine(DataDir, "instances");

    /// <summary>
    /// Archivos compartidos entre instancias (como en Modrinth): <c>versions</c>, <c>libraries</c>,
    /// <c>assets</c> y el Java de Mojang (<c>runtime</c>).
    /// </summary>
    public static string MetaDir => Path.Combine(DataDir, "meta");

    /// <summary>Carpeta oficial del launcher de Mojang: %APPDATA%\.minecraft.</summary>
    public static string DefaultGameDir
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
                appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
            return Path.Combine(appData, ".minecraft");
        }
    }

    /// <summary>
    /// Resuelve <c>game.path</c>: absoluta tal cual, relativa respecto a <see cref="LaunchDir"/>,
    /// vacía (o inválida) → <see cref="DefaultGameDir"/>. No toca el disco.
    /// </summary>
    public string ResolveGameDir(string? configuredPath)
    {
        var configured = configuredPath?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            try
            {
                return Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(LaunchDir, configured));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Si la ruta está mal formada, se usa la carpeta por defecto en lugar de fallar.
            }
        }

        return DefaultGameDir;
    }

    /// <summary>Resuelve una subcarpeta del juego (<c>versions.path</c>, etc.): absoluta o relativa al juego.</summary>
    public static string ResolveGameSubDir(string gameDir, string? configured, string defaultName)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? defaultName : configured.Trim();
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(gameDir, value));
    }

    /// <summary>True si la ruta es la <c>%APPDATA%\.minecraft</c> oficial, donde la limpieza no debe borrar nada.</summary>
    public static bool IsOfficialMinecraftDir(string path) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultGameDir)),
            StringComparison.OrdinalIgnoreCase);
}
