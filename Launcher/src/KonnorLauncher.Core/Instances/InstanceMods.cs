using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KonnorLauncher.Core.Instances;

/// <summary>
/// Un elemento de una carpeta de contenido (mods, shaderpacks, resourcepacks): un archivo activo (<c>.jar</c>,
/// <c>.zip</c>), desactivado (<c>.jar.disabled</c>, <c>.zip.disabled</c>) o una carpeta (siempre activa).
/// </summary>
/// <param name="FileName">Nombre en disco, tal cual.</param>
/// <param name="Name">Nombre que se muestra: el del archivo, sin <c>.disabled</c>.</param>
public sealed record ModFile(string FileName, string Name, bool Enabled, bool IsFolder = false);

/// <summary>Carpetas de contenido de una instancia que se ven en su panel.</summary>
public enum InstanceContentKind
{
    Mods,
    Shaders,
    ResourcePacks,
}

/// <summary>
/// Mods de una instancia. Desactivar un mod es renombrarlo a <c>.jar.disabled</c> (la convención de los launchers:
/// los cargadores solo leen los <c>.jar</c>), y activarlo, quitarle esa extensión.
/// </summary>
public static partial class InstanceMods
{
    public const string DisabledSuffix = ".disabled";
    private const int MaxMods = 500;

    /// <summary>Subcarpeta de la instancia de cada tipo de contenido.</summary>
    public static string FolderName(InstanceContentKind kind) => kind switch
    {
        InstanceContentKind.Shaders => "shaderpacks",
        InstanceContentKind.ResourcePacks => "resourcepacks",
        _ => "mods",
    };

    /// <summary>Mods de <paramref name="modsDir"/>, activos y desactivados, por nombre.</summary>
    public static IReadOnlyList<ModFile> List(string modsDir) => List(modsDir, InstanceContentKind.Mods);

    /// <summary>
    /// Contenido de una carpeta, por nombre: mods (<c>.jar</c>); shaders y paquetes de recursos (<c>.zip</c> y
    /// carpetas). Los desactivados (<c>.disabled</c>) también salen.
    /// </summary>
    public static IReadOnlyList<ModFile> List(string dir, InstanceContentKind kind)
    {
        if (!Directory.Exists(dir))
            return [];
        var files = Directory.EnumerateFiles(dir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Select(f => Parse(f, kind))
            .OfType<ModFile>();
        if (kind != InstanceContentKind.Mods)
        {
            files = files.Concat(Directory.EnumerateDirectories(dir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Select(d => new ModFile(d, d, Enabled: true, IsFolder: true)));
        }

        return files.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Take(MaxMods).ToList();
    }

    /// <summary>Lo que es un mod (<c>x.jar</c> o <c>x.jar.disabled</c>); null para cualquier otro archivo.</summary>
    public static ModFile? Parse(string fileName) => Parse(fileName, InstanceContentKind.Mods);

    /// <summary>Un archivo de contenido activo o desactivado de ese tipo; null si es otra cosa.</summary>
    public static ModFile? Parse(string fileName, InstanceContentKind kind)
    {
        var extension = kind == InstanceContentKind.Mods ? ".jar" : ".zip";
        if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return new ModFile(fileName, fileName, Enabled: true);
        if (fileName.EndsWith(extension + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            return new ModFile(fileName, fileName[..^DisabledSuffix.Length], Enabled: false);
        return null;
    }

    /// <summary>
    /// Activa o desactiva un mod y devuelve cómo queda. Si ya existe el archivo con el otro nombre (el mismo mod
    /// activo y desactivado a la vez), no se pisa: se lanza <see cref="IOException"/>.
    /// </summary>
    public static ModFile SetEnabled(string modsDir, ModFile mod, bool enabled)
    {
        if (mod.Enabled == enabled)
            return mod;
        if (mod.IsFolder)
            throw new ArgumentException("Las carpetas no se pueden desactivar", nameof(mod));
        var target = enabled ? mod.Name : mod.Name + DisabledSuffix;
        // Solo un nombre de archivo: nada de rutas que salgan de la carpeta mods.
        if (Path.GetFileName(mod.FileName) != mod.FileName || Path.GetFileName(target) != target)
            throw new ArgumentException("Nombre de mod no válido", nameof(mod));
        File.Move(Path.Combine(modsDir, mod.FileName), Path.Combine(modsDir, target), overwrite: false);
        return new ModFile(target, mod.Name, enabled);
    }

    // ── Icono ────────────────────────────────────────────────────────────

    private const long MaxIconBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Icono del mod, leído de dentro del <c>.jar</c>: el que declara <c>fabric.mod.json</c>, <c>quilt.mod.json</c> o
    /// <c>mods.toml</c> (Forge/NeoForge) y, si no, <c>pack.png</c>. null si no tiene o el archivo no es un jar válido.
    /// </summary>
    public static byte[]? ReadIcon(string jarPath)
    {
        try
        {
            // Paquete en carpeta: su pack.png
            if (Directory.Exists(jarPath))
            {
                var png = Path.Combine(jarPath, "pack.png");
                return File.Exists(png) && new FileInfo(png).Length <= MaxIconBytes ? File.ReadAllBytes(png) : null;
            }

            using var zip = ZipFile.OpenRead(jarPath);
            foreach (var candidate in IconCandidates(zip))
            {
                var entry = zip.GetEntry(candidate.TrimStart('/'));
                if (entry is null || entry.Length is <= 0 or > MaxIconBytes)
                    continue;
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                                       or NotSupportedException or ArgumentException)
        {
        }

        return null;
    }

    private static IEnumerable<string> IconCandidates(ZipArchive zip)
    {
        if (ReadText(zip, "fabric.mod.json") is { } fabric && IconFromJson(fabric, quilt: false) is { } fabricIcon)
            yield return fabricIcon;
        if (ReadText(zip, "quilt.mod.json") is { } quilt && IconFromJson(quilt, quilt: true) is { } quiltIcon)
            yield return quiltIcon;
        foreach (var toml in new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" })
        {
            if (ReadText(zip, toml) is { } text && LogoFile().Match(text) is { Success: true } match)
                yield return match.Groups[1].Value;
        }

        yield return "pack.png";
    }

    /// <summary><c>"icon": "ruta"</c> o <c>"icon": { "64": "ruta", "128": "ruta" }</c> (se elige el mayor hasta 128).</summary>
    private static string? IconFromJson(string json, bool quilt)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        if (quilt && root.TryGetProperty("quilt_loader", out var loader) && loader.TryGetProperty("metadata", out var metadata))
            root = metadata;
        if (!root.TryGetProperty("icon", out var icon))
            return null;
        if (icon.ValueKind == JsonValueKind.String)
            return icon.GetString();
        if (icon.ValueKind != JsonValueKind.Object)
            return null;
        return icon.EnumerateObject()
            .Select(p => (Size: int.TryParse(p.Name, out var size) ? size : 0, Path: p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null))
            .Where(p => p.Path is not null)
            .OrderBy(p => p.Size > 128 ? 1 : 0).ThenByDescending(p => p.Size)
            .Select(p => p.Path)
            .FirstOrDefault();
    }

    private static string? ReadText(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null || entry.Length > 1024 * 1024)
            return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    [GeneratedRegex("""^\s*logoFile\s*=\s*["']([^"']+)["']""", RegexOptions.Multiline)]
    private static partial Regex LogoFile();
}
