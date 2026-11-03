namespace KonnorLauncher.Core.Paths;

/// <summary>
/// Resolución de todas las rutas: la carpeta del launcher, la del juego y sus subcarpetas.
/// Están centralizadas para que el verificador y el lanzador usen siempre las mismas.
/// </summary>
public sealed class LauncherPaths
{
    /// <summary>Nombre del archivo de configuración junto al ejecutable.</summary>
    public const string PropertiesFileName = "launcher.properties";

    public LauncherPaths(string launchDir) => LaunchDir = Path.GetFullPath(launchDir);

    /// <summary>
    /// Carpeta del ejecutable (<c>LAUNCH_PATH</c>). En una publicación single-file,
    /// <see cref="AppContext.BaseDirectory"/> apunta a la carpeta del .exe, no a la de extracción.
    /// </summary>
    public string LaunchDir { get; }

    /// <summary>Ruta de <c>launcher.properties</c>.</summary>
    public string PropertiesPath => Path.Combine(LaunchDir, PropertiesFileName);

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

    /// <summary>
    /// Igual que <see cref="ResolveGameDir"/>, pero además crea la carpeta. Si la ruta configurada no se
    /// puede crear (permisos, unidad inexistente...), vuelve a la carpeta por defecto.
    /// </summary>
    public string EnsureGameDir(string? configuredPath)
    {
        var candidate = ResolveGameDir(configuredPath);
        try
        {
            Directory.CreateDirectory(candidate);
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var fallback = DefaultGameDir;
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    /// <summary>Resuelve una subcarpeta del juego (<c>versions.path</c>, etc.): absoluta o relativa al juego.</summary>
    public static string ResolveGameSubDir(string gameDir, string? configured, string defaultName)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? defaultName : configured.Trim();
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(gameDir, value));
    }

    /// <summary>
    /// Ruta para el botón 📁 de Opciones: relativa → relativa a la carpeta del launcher.
    /// Devuelve null si está vacía o es inválida.
    /// </summary>
    public string? ResolveUserFolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            var trimmed = value.Trim();
            return Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(LaunchDir, trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>True si la ruta es la <c>%APPDATA%\.minecraft</c> oficial, donde la limpieza no debe borrar nada.</summary>
    public static bool IsOfficialMinecraftDir(string path) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultGameDir)),
            StringComparison.OrdinalIgnoreCase);
}
