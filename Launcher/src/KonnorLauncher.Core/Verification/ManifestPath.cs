namespace KonnorLauncher.Core.Verification;

/// <summary>
/// Validación y codificación de las rutas del manifest. Es la parte que protege el disco del jugador
/// de un manifest malicioso o mal hecho.
/// </summary>
public static class ManifestPath
{
    private static readonly char[] ForbiddenChars = ['<', '>', '"', '|', '?', '*', ':'];

    /// <summary>
    /// Normaliza una ruta del manifest y la rechaza si es peligrosa: vacía, empieza por <c>/</c>,
    /// tiene un segmento vacío/<c>.</c>/<c>..</c>, es absoluta o empieza por una unidad (<c>C:</c>).
    /// También rechaza <c>:</c> en cualquier segmento (flujos NTFS), los caracteres <c>&lt;&gt;"|?*</c>
    /// y de control, y los segmentos que terminan en <c>.</c> o espacio (Windows los reinterpreta).
    /// </summary>
    /// <returns>(ruta relativa con separador del sistema, ruta con <c>/</c> para URL y ZIP).</returns>
    public static (string RelativePath, string UrlPath) Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new VerificationException("El manifest contiene una ruta vacía");

        var normalized = value.Replace('\\', '/');
        var parts = normalized.Split('/');
        var invalid =
            normalized.StartsWith('/')
            || Path.IsPathRooted(value)
            || (parts[0].Length == 2 && parts[0][1] == ':')
            || parts.Any(IsInvalidSegment);
        if (invalid)
            throw new VerificationException($"Ruta inválida en manifest: {value}");

        return (Path.Combine(parts), string.Join('/', parts));
    }

    private static bool IsInvalidSegment(string part) =>
        part.Length == 0
        || part is "." or ".."
        || part.IndexOfAny(ForbiddenChars) >= 0
        || part.Any(char.IsControl)
        || part.EndsWith('.')
        || part.EndsWith(' ');

    /// <summary>
    /// Ruta local absoluta dentro de la carpeta del juego; si quedara fuera, lanza una excepción.
    /// </summary>
    public static string ToLocalPath(string gameDir, ManifestFile file)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
        var full = Path.GetFullPath(Path.Combine(root, file.RelativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new VerificationException($"Ruta fuera del cliente: {file.UrlPath}");
        return full;
    }

    /// <summary>
    /// Codifica cada segmento por porcentaje y conserva las <c>/</c>.
    /// <see cref="Uri.EscapeDataString(string)"/> deja sin codificar los mismos caracteres que el
    /// <c>urllib.parse.quote</c> de Python (A-Z a-z 0-9 <c>-_.~</c>), así las URLs no cambian.
    /// </summary>
    public static string EncodeUrlPath(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    /// <summary>Clave de comparación para la limpieza: <c>/</c> normalizado y sin distinguir mayúsculas (NTFS).</summary>
    public static string ComparisonKey(string relativePath) =>
        relativePath.Replace('\\', '/').ToUpperInvariant();
}
