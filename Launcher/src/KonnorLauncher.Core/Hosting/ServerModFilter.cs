using System.Text.Json;
using KonnorLauncher.Core.Modrinth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Hosting;

/// <summary>
/// Mods del servidor que no se envían a los jugadores: los que Modrinth marca como «solo servidor»
/// (<c>client_side: unsupported</c>: administración, rendimiento del servidor…). En el cliente a veces
/// se ignoran, pero otras impiden que el juego arranque.
/// <list type="bullet">
/// <item>Cada mod se identifica por su SHA-512 (sirve también para los que no se instalaron desde Explorar).</item>
/// <item>Lo que responde Modrinth se guarda por hash en <c>.konnor/mod-sides.json</c>: solo se pregunta
/// por los mods nuevos o actualizados.</item>
/// <item>Los que no están en Modrinth, o si Modrinth no responde, se envían (no se puede saber).</item>
/// <item>Un mod que el administrador pone en <c>cliente/mods</c> se envía siempre.</item>
/// </list>
/// </summary>
public static class ServerModFilter
{
    /// <summary>Valor guardado para un archivo que no está en Modrinth.</summary>
    private const string NotOnModrinth = "";

    /// <summary>Nombres de los archivos de <paramref name="modsDir"/> que son solo de servidor.</summary>
    public static async Task<IReadOnlyList<string>> FindServerOnlyAsync(string modsDir, HashCache hashes, string cacheFile,
        ModrinthClient? modrinth, ILogger? logger = null, CancellationToken ct = default)
    {
        logger ??= NullLogger.Instance;
        if (!Directory.Exists(modsDir))
            return [];

        var mods = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
            .Select(path => (Name: Path.GetFileName(path), Sha512: hashes.Get(path, withSha512: true).Sha512!.ToLowerInvariant()))
            .ToList();
        var sides = Load(cacheFile);

        var missing = mods.Select(m => m.Sha512).Where(h => !sides.ContainsKey(h)).Distinct().ToList();
        if (missing.Count > 0 && modrinth is not null)
        {
            try
            {
                var versions = await modrinth.FindVersionsBySha512Async(missing, ct).ConfigureAwait(false);
                var projects = versions.Count == 0
                    ? []
                    : (await modrinth.GetProjectsAsync(versions.Values.Select(v => v.ProjectId), ct).ConfigureAwait(false))
                        .ToDictionary(p => p.Id, p => p.ClientSide ?? string.Empty);
                foreach (var hash in missing)
                {
                    if (!versions.TryGetValue(hash, out var version))
                        sides[hash] = NotOnModrinth;
                    else if (projects.TryGetValue(version.ProjectId, out var side))
                        sides[hash] = side;
                    // Versión sin proyecto en la respuesta: no se guarda y se vuelve a preguntar.
                }

                Save(cacheFile, sides, mods.Select(m => m.Sha512));
            }
            catch (ModrinthException ex)
            {
                logger.LogWarning(ex, "No se pudo consultar en Modrinth qué mods del servidor son solo de servidor");
            }
        }

        return [.. mods.Where(m => sides.TryGetValue(m.Sha512, out var side) && side == "unsupported").Select(m => m.Name)];
    }

    private static Dictionary<string, string> Load(string file)
    {
        try
        {
            if (File.Exists(file))
                return new Dictionary<string, string>(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Guarda solo los hashes de los mods que siguen en la carpeta.</summary>
    private static void Save(string file, Dictionary<string, string> sides, IEnumerable<string> present)
    {
        var keep = present.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(sides.Where(s => keep.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value)));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
