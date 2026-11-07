using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Players;

/// <summary>Skin guardada con «Descargar skin». <paramref name="Uuid"/>: la de MineSkin (para ver su página).</summary>
public sealed record DownloadedSkin(string Texture, string? Name, bool Slim, string? Uuid, DateTimeOffset DownloadedAt);

/// <summary>
/// Skins descargadas (sin aplicarlas): cada textura como <c>{hash}.png</c> y un índice
/// <c>downloaded.json</c> con su nombre y modelo, en <see cref="Directory"/>. No es caché: no se borra
/// sola. El archivo de la textura se llama por su hash, así que una misma skin solo se guarda una vez.
/// </summary>
public sealed class SkinLibrary
{
    public const string IndexFileName = "downloaded.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly ILogger _logger;
    private readonly object _sync = new();

    public SkinLibrary(string directory, ILogger<SkinLibrary>? logger = null)
    {
        Directory = Path.GetFullPath(directory);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string Directory { get; }

    private string IndexPath => Path.Combine(Directory, IndexFileName);

    /// <summary>Archivo de la textura (solo con un hash válido: nunca sale de la carpeta).</summary>
    public string TexturePath(string texture) =>
        MineSkinClient.IsTextureHash(texture)
            ? Path.Combine(Directory, texture + ".png")
            : throw new ArgumentException("La textura no es válida.", nameof(texture));

    /// <summary>Descargadas, de la más reciente a la más antigua. Salta las que ya no tienen su archivo.</summary>
    public IReadOnlyList<DownloadedSkin> LoadAll()
    {
        lock (_sync)
            return ReadIndex().Where(s => File.Exists(TexturePath(s.Texture))).OrderByDescending(s => s.DownloadedAt).ToList();
    }

    public bool Contains(string texture)
    {
        lock (_sync)
            return MineSkinClient.IsTextureHash(texture) && File.Exists(TexturePath(texture)) && ReadIndex().Any(s => s.Texture == texture);
    }

    /// <summary>
    /// Guarda la skin: copia <paramref name="sourcePng"/> y la apunta en el índice. Si ya estaba, se
    /// actualizan su nombre y su modelo y pasa a ser la más reciente.
    /// </summary>
    public DownloadedSkin Add(string sourcePng, string texture, string? name, bool slim, string? uuid)
    {
        var target = TexturePath(texture);
        var entry = new DownloadedSkin(texture, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), slim, uuid, DateTimeOffset.Now);
        lock (_sync)
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (!string.Equals(Path.GetFullPath(sourcePng), target, StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePng, target, overwrite: true);
            var index = ReadIndex().Where(s => s.Texture != texture).Append(entry).ToList();
            WriteIndex(index);
        }

        return entry;
    }

    /// <summary>
    /// Borra la skin descargada (su archivo y su entrada). La skin del jugador es una copia aparte: si
    /// estaba en uso, se sigue usando. Devuelve false si no estaba.
    /// </summary>
    public bool Remove(string texture)
    {
        if (!MineSkinClient.IsTextureHash(texture))
            return false;
        lock (_sync)
        {
            var index = ReadIndex();
            var removed = index.RemoveAll(s => s.Texture == texture) > 0;
            var file = TexturePath(texture);
            if (File.Exists(file))
            {
                File.Delete(file);
                removed = true;
            }

            if (removed && File.Exists(IndexPath))
                WriteIndex(index);
            return removed;
        }
    }

    private List<DownloadedSkin> ReadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath))
                return [];
            var list = JsonSerializer.Deserialize<List<DownloadedSkin>>(File.ReadAllText(IndexPath), Json) ?? [];
            return list.Where(s => MineSkinClient.IsTextureHash(s.Texture)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "No se pudo leer el índice de skins descargadas");
            return [];
        }
    }

    /// <summary>Escribe el índice pasando por un temporal: nunca queda a medias.</summary>
    private void WriteIndex(List<DownloadedSkin> index)
    {
        var temp = IndexPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(index, Json));
        File.Move(temp, IndexPath, overwrite: true);
    }
}
