using System.Text.Json;

namespace KonnorLauncher.Core.Hosting;

/// <summary>
/// Jugadores que entraron alguna vez a un servidor: Minecraft guarda un archivo por jugador en
/// <c>&lt;mundo&gt;/playerdata/&lt;uuid&gt;.dat</c> (desde la 26.1, en <c>&lt;mundo&gt;/players/data</c>) y el nombre de
/// cada UUID en <c>usercache.json</c>.
/// (<c>usercache.json</c> solo no basta: también apunta los nombres que se buscan con <c>/op</c> o la lista blanca.)
/// </summary>
public static class PlayerHistory
{
    /// <summary>Nombres de los jugadores que entraron alguna vez, por orden alfabético.</summary>
    public static IReadOnlyList<string> Load(string serverDir)
    {
        var level = ServerPropertiesFile.Load(Path.Combine(serverDir, ServerPropertiesFile.FileName)).Get("level-name", "world");
        var world = Path.Combine(serverDir, string.IsNullOrWhiteSpace(level) ? "world" : level);
        var folders = new[] { Path.Combine(world, "playerdata"), Path.Combine(world, "players", "data") }.Where(Directory.Exists).ToList();
        if (folders.Count == 0)
            return [];

        var names = ReadUserCache(Path.Combine(serverDir, "usercache.json"));
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in folders.SelectMany(f => Directory.EnumerateFiles(f, "*.dat")))
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var uuid) && names.TryGetValue(uuid, out var name))
                result.Add(name);
        }

        return [.. result];
    }

    /// <summary><c>[{"name": "Steve", "uuid": "…", "expiresOn": "…"}]</c> → UUID → nombre. Dañado o ausente: vacío.</summary>
    public static Dictionary<Guid, string> ReadUserCache(string path)
    {
        var result = new Dictionary<Guid, string>();
        try
        {
            if (!File.Exists(path))
                return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("uuid", out var uuid) && Guid.TryParse(uuid.GetString(), out var id)
                    && entry.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text)
                    result[id] = text;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }
}
