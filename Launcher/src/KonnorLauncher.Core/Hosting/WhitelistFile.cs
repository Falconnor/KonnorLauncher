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
        Write(path, array);
        return true;
    }

    /// <summary>¿Está <paramref name="name"/> en la lista? (sin distinguir mayúsculas)</summary>
    public static bool Contains(string path, string name) =>
        Load(path).Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Añade una entrada (<c>uuid</c>, <c>name</c> y los campos de <paramref name="extra"/>) si el jugador no estaba.
    /// Devuelve si se añadió. Es lo que hace el servidor con <c>/whitelist add</c>, <c>/ban</c> u <c>/op</c>, para
    /// cuando está detenido.
    /// </summary>
    public static bool Add(string path, string name, Guid uuid, IReadOnlyDictionary<string, JsonNode?>? extra = null)
    {
        var array = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonArray existing ? existing : [];
        if (array.OfType<JsonObject>().Any(e => string.Equals(Text(e, "name"), name, StringComparison.OrdinalIgnoreCase)))
            return false;
        var entry = new JsonObject { ["uuid"] = uuid.ToString("D"), ["name"] = name };
        foreach (var (key, value) in extra ?? new Dictionary<string, JsonNode?>())
            entry[key] = value;
        array.Add(entry);
        Write(path, array);
        return true;
    }

    private static void Write(string path, JsonArray array)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
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
    public static bool Add(string serverDir, string name, Guid uuid) => PlayerListFile.Add(PathFor(serverDir), name, uuid);
}

/// <summary><c>banned-players.json</c>.</summary>
public static class BannedPlayersFile
{
    public const string FileName = "banned-players.json";

    public static string PathFor(string serverDir) => Path.Combine(serverDir, FileName);
    public static IReadOnlyList<WhitelistEntry> Load(string serverDir) => PlayerListFile.Load(PathFor(serverDir));
    public static bool Remove(string serverDir, string name) => PlayerListFile.Remove(PathFor(serverDir), name);

    /// <summary>Baneo permanente, con el formato de fecha del servidor (<c>2026-11-11 21:30:00 +0100</c>), como <c>/ban</c>.</summary>
    public static bool Add(string serverDir, string name, Guid uuid, DateTimeOffset now) => PlayerListFile.Add(PathFor(serverDir), name, uuid,
        new Dictionary<string, JsonNode?>
        {
            ["created"] = now.ToString("yyyy-MM-dd HH:mm:ss ", System.Globalization.CultureInfo.InvariantCulture) + now.ToString("zzz", System.Globalization.CultureInfo.InvariantCulture).Replace(":", string.Empty),
            ["source"] = "Konnor Launcher",
            ["expires"] = "forever",
            ["reason"] = "Banned by an operator.",
        });
}

/// <summary><c>ops.json</c>: operadores (<c>uuid</c>, <c>name</c>, <c>level</c>, <c>bypassesPlayerLimit</c>).</summary>
public static class OpsFile
{
    public const string FileName = "ops.json";

    public static string PathFor(string serverDir) => Path.Combine(serverDir, FileName);
    public static IReadOnlyList<WhitelistEntry> Load(string serverDir) => PlayerListFile.Load(PathFor(serverDir));

    /// <summary>Operador de nivel 4 (todos los comandos), como <c>/op</c>.</summary>
    public static bool Add(string serverDir, string name, Guid uuid) => PlayerListFile.Add(PathFor(serverDir), name, uuid,
        new Dictionary<string, JsonNode?> { ["level"] = 4, ["bypassesPlayerLimit"] = false });

    public static bool Remove(string serverDir, string name) => PlayerListFile.Remove(PathFor(serverDir), name);
}
