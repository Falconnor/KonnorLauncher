using System.Text.Json;
using System.Text.Json.Nodes;

namespace KonnorLauncher.Core.Hosting;

/// <summary>Jugador de la lista blanca.</summary>
public sealed record WhitelistEntry(string Name, string Uuid);

/// <summary>
/// <c>whitelist.json</c> del servidor: <c>[{"uuid": "…", "name": "Steve"}]</c>. Con el servidor en marcha los cambios se
/// hacen con <c>/whitelist</c> (el servidor tiene la lista en memoria y reescribe el archivo); detenido, se edita aquí.
/// </summary>
public static class WhitelistFile
{
    public const string FileName = "whitelist.json";

    public static string PathFor(string serverDir) => Path.Combine(serverDir, FileName);

    /// <summary>Jugadores de la lista, por nombre. Dañada o ausente: vacía.</summary>
    public static IReadOnlyList<WhitelistEntry> Load(string serverDir)
    {
        try
        {
            if (!File.Exists(PathFor(serverDir)))
                return [];
            if (JsonNode.Parse(File.ReadAllText(PathFor(serverDir))) is not JsonArray array)
                return [];
            return [.. array.OfType<JsonObject>()
                .Select(e => new WhitelistEntry(e["name"]?.GetValue<string>() ?? string.Empty, e["uuid"]?.GetValue<string>() ?? string.Empty))
                .Where(e => e.Name.Length > 0)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Quita a <paramref name="name"/> del archivo (sin distinguir mayúsculas). Devuelve si estaba.</summary>
    public static bool Remove(string serverDir, string name)
    {
        var path = PathFor(serverDir);
        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array)
            return false;
        var match = array.OfType<JsonObject>()
            .FirstOrDefault(e => string.Equals(e["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;
        array.Remove(match);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
        return true;
    }
}
