namespace KonnorLauncher.Core.Launching;

/// <summary>
/// Selección de Java. La versión anterior usaba el Java más nuevo del sistema para todo y las
/// versiones antiguas (1.16 o anteriores) fallaban con Java 17/21. Ahora:
/// 1. <c>java.path</c> vacío o genérico (<c>java</c>, <c>java.exe</c>, <c>javaw</c>, <c>javaw.exe</c>)
///    → Java de Mojang gestionado por CmlLib (el correcto para cada versión).
/// 2. Ruta relativa → relativa a la carpeta del juego.
/// 3. Si el archivo existe → anulación manual.
/// 4. Si no existe → Java de Mojang (con un aviso en el log).
/// </summary>
public static class JavaSelector
{
    private static readonly string[] GenericCommands = ["java", "java.exe", "javaw", "javaw.exe"];

    /// <summary>Resultado: ruta de anulación (null = Java de Mojang) y si se ignoró una ruta configurada inexistente.</summary>
    public readonly record struct Selection(string? OverridePath, bool ConfiguredPathMissing);

    public static bool IsAutomatic(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
        || GenericCommands.Contains(configured.Trim(), StringComparer.OrdinalIgnoreCase);

    public static Selection Resolve(string? configured, string gameDir)
    {
        if (IsAutomatic(configured))
            return new Selection(null, false);

        string full;
        try
        {
            var value = configured!.Trim().Trim('"');
            full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(gameDir, value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new Selection(null, true);
        }

        return File.Exists(full) ? new Selection(full, false) : new Selection(null, true);
    }
}
