using System.Text.Json;
using System.Text.Json.Nodes;

namespace KonnorLauncher.Core.Hosting;

/// <summary>Jugador de una lista del servidor (lista blanca o baneados). <paramref name="Reason"/>, quién y cuándo: solo en baneos.</summary>
public sealed record WhitelistEntry(string Name, string Uuid, string? Reason = null, string? Source = null, string? Created = null, string? Expires = null)
{
    /// <summary>«Motivo · por quién · hasta cuándo» (baneados); vacío en la lista blanca.</summary>
    public string Details => string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(Reason) ? null : Reason,
        string.IsNullOrWhiteSpace(Source) ? null : $"por {Source}",
        string.IsNullOrWhiteSpace(Expires) || Expires == "forever" ? null : $"hasta {Expires}",
    }.Where(t => t is not null));
}

/// <summary>
/// Listas de jugadores del servidor en JSON: <c>whitelist.json</c> (<c>[{"uuid", "name"}]</c>) y
/// <c>banned-players.json</c> (además <c>reason</c>, <c>source</c>, <c>created</c>, <c>expires</c>). Con el servidor en
/// marcha los cambios se hacen con comandos (el servidor tiene la lista en memoria y reescribe el archivo); detenido,
/// se edita el archivo.
/// </summary>
public static class PlayerListFile
{
    public static IReadOnlyList<WhitelistEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array)
                return [];
            return [.. array.OfType<JsonObject>()
                .Select(e => new WhitelistEntry(Text(e, "name") ?? string.Empty, Text(e, "uuid") ?? string.Empty,
                    Text(e, "reason"), Text(e, "source"), Text(e, "created"), Text(e, "expires")))
                .Where(e => e.Name.Length > 0)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Quita a <paramref name="name"/> (sin distinguir mayúsculas). Devuelve si estaba.</summary>
    public static bool Remove(string path, string name)
    {
        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array)
            return false;
        var match = array.OfType<JsonObject>().FirstOrDefault(e => string.Equals(Text(e, "name"), name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;
        array.Remove(match);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    private static string? Text(JsonObject entry, string name) =>
        entry[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary><c>whitelist.json</c>.</summary>
public static class WhitelistFile
{
    public const string FileName = "whitelist.json";

    public static string PathFor(string serverDir) => Path.Combine(serverDir, FileName);
    public static IReadOnlyList<WhitelistEntry> Load(string serverDir) => PlayerListFile.Load(PathFor(serverDir));
    public static bool Remove(string serverDir, string name) => PlayerListFile.Remove(PathFor(serverDir), name);
}

/// <summary><c>banned-players.json</c>.</summary>
public static class BannedPlayersFile
{
    public const string FileName = "banned-players.json";

    public static string PathFor(string serverDir) => Path.Combine(serverDir, FileName);
    public static IReadOnlyList<WhitelistEntry> Load(string serverDir) => PlayerListFile.Load(PathFor(serverDir));
    public static bool Remove(string serverDir, string name) => PlayerListFile.Remove(PathFor(serverDir), name);
}
