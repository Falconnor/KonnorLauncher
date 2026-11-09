using System.IO;
using System.IO.Compression;
using KonnorLauncher.Core.Paths;

namespace KonnorLauncher.App.Services;

/// <summary>
/// Skins predeterminadas de Minecraft (Steve, Alex y las otras siete), cada una con brazos clásicos y finos. No se
/// incluyen en el launcher: se extraen una vez del jar de cliente que ya haya descargado (1.19.3 en adelante, en
/// <c>textures/entity/player/wide|slim</c>) y se guardan en la caché. No se pueden borrar desde Skins.
/// </summary>
public static class DefaultSkins
{
    private const string Prefix = "assets/minecraft/textures/entity/player/";
    private const string KeyPrefix = "default:";

    /// <summary>(archivo, nombre, modelo con el que se muestra al principio: Alex con brazos finos, el resto clásicos).</summary>
    public static readonly IReadOnlyList<(string Id, string Name, bool Slim)> All =
    [
        ("steve", "Steve", false), ("alex", "Alex", true), ("ari", "Ari", false), ("efe", "Efe", false), ("kai", "Kai", false),
        ("makena", "Makena", false), ("noor", "Noor", false), ("sunny", "Sunny", false), ("zuri", "Zuri", false),
    ];

    private static readonly Lazy<bool> Extracted = new(Extract);

    /// <summary>Están en la caché (si no había ningún jar 1.19.3+, no).</summary>
    public static bool Available => Extracted.Value;

    /// <summary>Clave de textura de una predeterminada: <c>default:steve:wide</c>.</summary>
    public static string Key(string id, bool slim) => $"{KeyPrefix}{id}:{(slim ? "slim" : "wide")}";

    public static bool IsKey(string texture) => texture.StartsWith(KeyPrefix, StringComparison.Ordinal);

    /// <summary>Id (<c>steve</c>) de una clave.</summary>
    public static string IdOf(string key) => key[KeyPrefix.Length..].Split(':')[0];

    /// <summary>Archivo en la caché de una clave <c>default:…</c>.</summary>
    public static string PathFor(string key)
    {
        var parts = key[KeyPrefix.Length..].Split(':');
        return Path.Combine(CacheDir, $"{parts[1]}_{parts[0]}.png");
    }

    private static string CacheDir => Path.Combine(LauncherPaths.CacheDir, "default-skins");

    private static IEnumerable<string> AllKeys() => All.SelectMany(s => new[] { Key(s.Id, false), Key(s.Id, true) });

    private static bool Extract()
    {
        if (AllKeys().All(k => File.Exists(PathFor(k))))
            return true;
        foreach (var jar in HudSprites.ClientJars())
        {
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                var entries = AllKeys().Select(k => (Key: k, Entry: zip.GetEntry(EntryName(k)))).ToList();
                if (entries.Any(e => e.Entry is null))
                    continue; // anterior a la 1.19.3: solo Steve y Alex, en otra ruta
                Directory.CreateDirectory(CacheDir);
                foreach (var (key, entry) in entries)
                    entry!.ExtractToFile(PathFor(key), overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }

        return false;
    }

    private static string EntryName(string key)
    {
        var parts = key[KeyPrefix.Length..].Split(':');
        return $"{Prefix}{parts[1]}/{parts[0]}.png";
    }
}
