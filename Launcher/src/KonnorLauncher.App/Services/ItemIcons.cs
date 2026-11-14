using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KonnorLauncher.App.Services;

/// <summary>
/// Icono de un objeto del juego (<c>minecraft:diamond_boots</c>, <c>minecraft:spruce_planks</c>…) para el perfil de los
/// jugadores, leído del jar de cliente que ya haya descargado el launcher (el mismo que usa <see cref="HudSprites"/>).
/// Los objetos tienen su textura en <c>textures/item</c>; los bloques, en <c>textures/block</c> (de un bloque con varias
/// caras se usa el frente o el lateral). Escaleras, losas, vallas… no tienen textura propia: se usa la de su material.
/// Los de mods (otro espacio de nombres) no se encuentran: null.
/// </summary>
public static class ItemIcons
{
    private const string Textures = "assets/minecraft/textures/";

    /// <summary>Formas de bloque cuya textura es la del material (spruce_stairs → spruce_planks, stone_brick_slab → stone_bricks).</summary>
    private static readonly string[] Shapes =
        ["_stairs", "_slab", "_fence_gate", "_fence", "_wall", "_button", "_pressure_plate", "_sign", "_hanging_sign", "_door", "_trapdoor"];

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);
    private static readonly object ZipLock = new();
    private static ZipArchive? _zip;
    private static bool _opened;

    /// <summary>Icono del objeto; null si no hay jar de cliente o no tiene textura (los de mods).</summary>
    public static ImageSource? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var name = id.StartsWith("minecraft:", StringComparison.Ordinal) ? id["minecraft:".Length..] : id.Contains(':') ? null : id;
        return name is null ? null : Cache.GetOrAdd(name, Load);
    }

    private static ImageSource? Load(string name)
    {
        lock (ZipLock)
        {
            if (!_opened)
            {
                _opened = true;
                _zip = OpenClientJar();
            }

            if (_zip is null)
                return null;
            foreach (var candidate in Candidates(name))
            {
                if (_zip.GetEntry(Textures + candidate + ".png") is not { } entry)
                    continue;
                try
                {
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    return Decode(buffer.ToArray());
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or FileFormatException
                                               or ArgumentException)
                {
                    return null;
                }
            }

            return null;
        }
    }

    /// <summary>Dónde puede estar la textura de un objeto, por orden.</summary>
    internal static IEnumerable<string> Candidates(string name)
    {
        yield return "item/" + name;
        yield return "block/" + name;
        foreach (var face in new[] { "_front", "_side", "_top" })
            yield return "block/" + name + face;
        if (Shapes.FirstOrDefault(name.EndsWith) is { } shape)
        {
            var material = name[..^shape.Length];
            yield return "block/" + material + "_planks"; // madera: spruce_stairs → spruce_planks
            yield return "block/" + material + "s";       // stone_brick_slab → stone_bricks
            yield return "block/" + material;              // stone_stairs → stone
        }

        // Bloques cuya textura no lleva «_block»: magma_block → magma
        if (name.EndsWith("_block", StringComparison.Ordinal))
            yield return "block/" + name[..^"_block".Length];

        // Troncos y madera: oak_wood → oak_log; stripped_oak_wood → stripped_oak_log
        if (name.EndsWith("_wood", StringComparison.Ordinal))
            yield return "block/" + name[..^"_wood".Length] + "_log";
    }

    private static ZipArchive? OpenClientJar()
    {
        foreach (var jar in HudSprites.ClientJars())
        {
            try
            {
                var zip = ZipFile.OpenRead(jar);
                if (zip.GetEntry(Textures + "item/diamond.png") is not null)
                    return zip;
                zip.Dispose();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }

        return null;
    }

    /// <summary>
    /// PNG congelado. Las texturas animadas (lava, magma…) son una tira vertical de fotogramas: se toma el primero.
    /// </summary>
    private static ImageSource Decode(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        if (image.PixelHeight <= image.PixelWidth)
            return image;
        var frame = new CroppedBitmap(image, new Int32Rect(0, 0, image.PixelWidth, image.PixelWidth));
        frame.Freeze();
        return frame;
    }
}
