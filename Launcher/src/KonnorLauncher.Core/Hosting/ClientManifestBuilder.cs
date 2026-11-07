using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Verification;

namespace KonnorLauncher.Core.Hosting;

/// <summary>Resultado de generar el manifest del cliente.</summary>
public sealed record ManifestBuildResult(
    bool Changed,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> InvalidPaths,
    IReadOnlyDictionary<string, string> PhysicalFiles,
    byte[] ManifestBytes,
    TimeSpan Elapsed);

/// <summary>
/// Genera <c>client_manifest.json</c> como lo hacía LauncherBackEnd (<c>generate_manifest.py</c>), con el
/// formato que valida <see cref="ManifestParser"/>:
/// <list type="bullet">
/// <item><c>minecraft</c>: versión y cargador del servidor. Con ella el launcher instala el juego desde
/// Mojang, así que nunca se publican <c>versions</c>, <c>libraries</c> ni <c>assets</c> (ni bloques ZIP).</item>
/// <item><c>files</c>: todo lo que hay en la carpeta <c>cliente</c> y, si se pide, los mods del servidor.
/// Los archivos de primer nivel de <c>mods</c>, <c>resourcepacks</c> y <c>shaderpacks</c> llevan su
/// SHA-512 para que el launcher los descargue de Modrinth cuando estén allí.</item>
/// <item>La política de sincronización (<c>delete_unauthorized</c>, <c>always_online</c>...).</item>
/// </list>
/// Las rutas que el launcher rechazaría no se publican: se devuelven en <see cref="ManifestBuildResult.InvalidPaths"/>.
/// </summary>
public static class ClientManifestBuilder
{
    private static readonly HashSet<string> ExcludedTopLevel = new(StringComparer.OrdinalIgnoreCase) { "versions", "libraries", "assets", "packages" };
    private static readonly HashSet<string> ModrinthFolders = new(StringComparer.OrdinalIgnoreCase) { "mods", "resourcepacks", "shaderpacks" };
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase) { "Thumbs.db", "desktop.ini", ".DS_Store" };

    /// <summary>
    /// Archivos que se publican: ruta del manifest (con <c>/</c>) → archivo en disco. Los de la carpeta del
    /// cliente tienen prioridad sobre los mods del servidor con el mismo nombre.
    /// </summary>
    public static (SortedDictionary<string, string> Files, List<string> Invalid) CollectFiles(
        string clientDir, string? serverModsDir)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var invalid = new List<string>();

        if (serverModsDir is not null && Directory.Exists(serverModsDir))
        {
            foreach (var path in Directory.EnumerateFiles(serverModsDir, "*.jar", SearchOption.TopDirectoryOnly))
                Add("mods/" + Path.GetFileName(path), path);
        }

        if (Directory.Exists(clientDir))
        {
            // La ruta relativa se corta del texto tal cual: GetRelativePath normaliza y en Windows quitaría
            // el punto final de un nombre, que es justo lo que hay que detectar.
            var root = Path.TrimEndingDirectorySeparator(clientDir);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = path[(root.Length + 1)..].Replace('\\', '/');
                if (ExcludedTopLevel.Contains(relative.Split('/')[0]))
                    continue;
                Add(relative, path);
            }
        }

        return (files, invalid);

        void Add(string relative, string path)
        {
            var name = Path.GetFileName(path);
            if (ExcludedNames.Contains(name) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                ManifestPath.Normalize(relative);
            }
            catch (VerificationException)
            {
                invalid.Add(relative);
                return;
            }

            // Mismo nombre ignorando mayúsculas (Windows): gana el último (la carpeta del cliente).
            var existing = files.Keys.FirstOrDefault(k => string.Equals(k, relative, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                files.Remove(existing);
            files[relative] = path;
        }
    }

    /// <summary>Genera el manifest y lo escribe en <paramref name="manifestPath"/> solo si cambió.</summary>
    public static ManifestBuildResult Build(HostedServer server, string serverDir, string clientDir, string manifestPath, HashCache cache)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var modsDir = server.Publish.IncludeServerMods && server.IsModded ? Path.Combine(serverDir, "mods") : null;
        var (files, invalid) = CollectFiles(clientDir, modsDir);

        var filesNode = new JsonObject();
        long total = 0;
        foreach (var (relative, path) in files)
        {
            var parts = relative.Split('/');
            var withSha512 = parts.Length == 2 && ModrinthFolders.Contains(parts[0]);
            var (sha256, sha512, size) = cache.Get(path, withSha512);
            total += size;
            var entry = new JsonObject { ["sha256"] = sha256 };
            if (sha512 is not null)
                entry["sha512"] = sha512;
            entry["size"] = size;
            filesNode[relative] = entry;
        }

        cache.Save();

        var manifest = new JsonObject();
        if (!string.IsNullOrWhiteSpace(server.Publish.ClientVersion))
            manifest["client_version"] = server.Publish.ClientVersion.Trim();
        manifest["delete_unauthorized"] = server.Publish.DeleteUnauthorized;
        manifest["always_online"] = server.Publish.AlwaysOnline;
        manifest["lock_server_url"] = server.Publish.LockServerUrl;
        manifest["protect_packs"] = server.Publish.ProtectPacks;
        manifest["released"] = false;
        manifest["auto_join"] = server.Publish.AutoJoin && !string.IsNullOrWhiteSpace(server.Publish.JoinAddress);
        if (server.Publish.AutoJoin && !string.IsNullOrWhiteSpace(server.Publish.JoinAddress))
            manifest["server_address"] = server.Publish.JoinAddress;
        manifest["minecraft"] = GameNode(server);
        manifest["files"] = filesNode;
        manifest["asset_blocks"] = new JsonObject();

        var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        var changed = !File.Exists(manifestPath) || !File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(bytes);
        if (changed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var tmp = manifestPath + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, manifestPath, overwrite: true);
        }

        return new ManifestBuildResult(changed, files.Count, total, invalid, files, bytes, started.Elapsed);
    }

    /// <summary>
    /// <c>{"version", "loader", "loader_version"}</c>. Paper y Vanilla publican el juego sin cargador: los
    /// plugins de Paper solo van en el servidor.
    /// </summary>
    public static JsonObject GameNode(HostedServer server)
    {
        var loader = server.ClientLoader;
        var node = new JsonObject
        {
            ["version"] = server.MinecraftVersion,
            ["loader"] = loader == ModLoader.Vanilla ? "vanilla" : loader.ToString().ToLowerInvariant(),
        };
        if (loader != ModLoader.Vanilla)
            node["loader_version"] = server.LoaderVersion;
        return node;
    }
}

/// <summary>
/// Caché de hashes por archivo (tamaño + fecha de modificación), como <c>hash_cache.py</c>: si ninguno
/// cambió, el hash se reutiliza y no hay que volver a leer el archivo.
/// </summary>
public sealed class HashCache
{
    private sealed record Entry(long Size, long Modified, string Sha256, string? Sha512);

    private readonly string _file;
    private readonly Dictionary<string, Entry> _entries;
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public HashCache(string file)
    {
        _file = file;
        try
        {
            _entries = File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _entries = [];
        }

        _entries = new Dictionary<string, Entry>(_entries, StringComparer.OrdinalIgnoreCase);
    }

    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public (string Sha256, string? Sha512, long Size) Get(string path, bool withSha512)
    {
        var info = new FileInfo(path);
        var key = info.FullName;
        lock (_sync)
        {
            _used.Add(key);
            if (_entries.TryGetValue(key, out var entry) && entry.Size == info.Length
                && entry.Modified == info.LastWriteTimeUtc.Ticks && (!withSha512 || entry.Sha512 is not null))
            {
                Hits++;
                return (entry.Sha256, withSha512 ? entry.Sha512 : null, entry.Size);
            }
        }

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = withSha512 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA512) : null;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha256.AppendData(buffer, 0, read);
                sha512?.AppendData(buffer, 0, read);
            }
        }

        var result = new Entry(info.Length, info.LastWriteTimeUtc.Ticks,
            Convert.ToHexStringLower(sha256.GetHashAndReset()), sha512 is null ? null : Convert.ToHexStringLower(sha512.GetHashAndReset()));
        lock (_sync)
        {
            Misses++;
            _entries[key] = result;
        }

        return (result.Sha256, result.Sha512, result.Size);
    }

    /// <summary>Guarda solo las entradas usadas en esta pasada (las de archivos borrados desaparecen).</summary>
    public void Save()
    {
        Dictionary<string, Entry> used;
        lock (_sync)
            used = _entries.Where(e => _used.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(used));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
