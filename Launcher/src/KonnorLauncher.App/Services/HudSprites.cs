using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KonnorLauncher.Core.Paths;

namespace KonnorLauncher.App.Services;

/// <summary>
/// Iconos del HUD del juego (corazones, muslos, armadura, burbujas y barra de experiencia) para el perfil de los
/// jugadores. No se incluyen en el launcher: se extraen una vez del jar de cliente que ya haya descargado (1.20.2 en
/// adelante, que guarda cada icono en <c>textures/gui/sprites/hud</c>) y se guardan en la caché. Si no hay ninguno,
/// <see cref="Available"/> es false y el perfil muestra barras.
/// </summary>
public static class HudSprites
{
    private const string Prefix = "assets/minecraft/textures/gui/sprites/hud/";

    /// <summary>Iconos que se usan (ruta dentro de <see cref="Prefix"/>).</summary>
    private static readonly string[] Names =
    [
        "heart/container.png", "heart/full.png", "heart/half.png", "heart/absorbing_full.png", "heart/absorbing_half.png",
        "food_empty.png", "food_full.png", "food_half.png",
        "armor_empty.png", "armor_full.png", "armor_half.png",
        "air.png", "air_empty.png",
        "experience_bar_background.png", "experience_bar_progress.png",
    ];

    private static readonly Lazy<Dictionary<string, BitmapSource>> Sprites = new(Load);

    public static bool Available => Sprites.Value.Count == Names.Length;

    public static ImageSource? Get(string name) => Sprites.Value.GetValueOrDefault(name);

    private static string CacheDir => Path.Combine(LauncherPaths.CacheDir, "hud");

    private static Dictionary<string, BitmapSource> Load()
    {
        try
        {
            if (!Names.All(n => File.Exists(CachePath(n))))
                ExtractFromClientJar();
            var result = new Dictionary<string, BitmapSource>();
            foreach (var name in Names)
            {
                if (File.Exists(CachePath(name)))
                    result[name] = Decode(File.ReadAllBytes(CachePath(name)));
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException
                                       or FileFormatException)
        {
            return [];
        }
    }

    private static string CachePath(string name) => Path.Combine(CacheDir, name.Replace('/', '_'));

    /// <summary>Del jar de cliente más reciente que tenga los iconos (versiones compartidas o de una instancia).</summary>
    private static void ExtractFromClientJar()
    {
        foreach (var jar in ClientJars())
        {
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                var entries = Names.Select(n => zip.GetEntry(Prefix + n)).ToList();
                if (entries.Any(e => e is null))
                    continue; // anterior a la 1.20.2: los iconos están en otra imagen
                Directory.CreateDirectory(CacheDir);
                for (var i = 0; i < Names.Length; i++)
                    entries[i]!.ExtractToFile(CachePath(Names[i]), overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }

    /// <summary><c>versions/&lt;id&gt;/&lt;id&gt;.jar</c> (los de servidor y cargadores no llevan texturas), el más reciente primero.</summary>
    private static IEnumerable<string> ClientJars()
    {
        var roots = new List<string> { Path.Combine(LauncherPaths.MetaDir, "versions") };
        if (Directory.Exists(LauncherPaths.InstancesDir))
            roots.AddRange(Directory.EnumerateDirectories(LauncherPaths.InstancesDir).Select(d => Path.Combine(d, "versions")));
        return roots
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(dir => Path.Combine(dir, Path.GetFileName(dir) + ".jar"))
            .Where(File.Exists)
            .Where(jar => new FileInfo(jar).Length > 5_000_000) // los de Fabric/Quilt son pequeños y sin texturas
            .OrderByDescending(File.GetLastWriteTimeUtc);
    }

    private static BitmapSource Decode(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
