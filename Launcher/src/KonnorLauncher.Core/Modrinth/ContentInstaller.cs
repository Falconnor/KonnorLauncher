using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using KonnorLauncher.Core.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Modrinth;

/// <summary>Progreso de una instalación: texto y porcentaje (null = sin porcentaje).</summary>
public sealed record InstallProgress(string Message, double? Percent);

/// <summary>Resultado de instalar un proyecto: su archivo, las dependencias añadidas y las que no se encontraron.</summary>
public sealed record InstallResult(string FileName, IReadOnlyList<string> Dependencies, IReadOnlyList<string> MissingDependencies);

/// <summary>
/// Instala contenido de Modrinth: mods, paquetes de recursos y shaders en una instancia, data packs en un
/// mundo, y modpacks (o servidores, que son modpacks) como una instancia nueva.
/// <para>
/// Todo lo descargado se comprueba con su hash (SHA-512 o SHA-1) y no puede superar el tamaño
/// anunciado. Los nombres y rutas vienen de Internet: no pueden salir de la instancia ni pisar su
/// <c>instance.json</c>.
/// </para>
/// </summary>
public sealed class ContentInstaller
{
    /// <summary>Qué se instaló desde Modrinth en cada instancia (para saber qué está instalado y actualizarlo).</summary>
    public const string IndexFileName = ".konnor-modrinth.json";

    /// <summary>Tope de un archivo sin tamaño anunciado y de cada entrada de los <c>overrides</c> de un modpack.</summary>
    public const long MaxUnknownFileBytes = 1024L * 1024 * 1024;

    private const int MaxDependencyDepth = 6;
    private const int ParallelDownloads = 4;

    private readonly ModrinthClient _client;
    private readonly InstanceStore _store;
    private readonly ILogger _logger;

    public ContentInstaller(ModrinthClient client, InstanceStore store, ILogger<ContentInstaller>? logger = null)
    {
        _client = client;
        _store = store;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    // ── Compatibilidad ──────────────────────────────────────────────────────

    /// <summary>Cargadores aceptados para este tipo en la instancia (null = cualquiera).</summary>
    public static IReadOnlyList<string>? CompatibleLoaders(GameInstance instance, ContentType type) => type switch
    {
        ContentType.Mod => instance.Display.Loader switch
        {
            ModLoader.Fabric => ["fabric"],
            ModLoader.Quilt => ["quilt", "fabric"], // Quilt también carga mods de Fabric
            ModLoader.Forge => ["forge"],
            ModLoader.NeoForge => ["neoforge"],
            _ => [],
        },
        ContentType.ResourcePack => ["minecraft"],
        ContentType.DataPack => ["datapack"],
        _ => null,
    };

    /// <summary>Versión de Minecraft de la instancia (la que publica el servidor en las sincronizadas).</summary>
    public static string? GameVersion(GameInstance instance) =>
        instance.Display.Minecraft is { Length: > 0 } version ? version : null;

    /// <summary>Por qué no se puede instalar este tipo en la instancia, o null si se puede.</summary>
    public static string? BlockReason(GameInstance instance, ContentType type) => type switch
    {
        ContentType.Mod when instance.SyncWithServer =>
            "Los mods de esta instancia los decide su servidor: lo que añadas se borraría al jugar.",
        ContentType.Mod when instance.Display.Loader == ModLoader.Vanilla =>
            "Esta instancia no tiene cargador de mods (es vanilla). Elige una con Fabric, Forge, NeoForge o Quilt.",
        ContentType.ResourcePack or ContentType.Shader when instance.IsPacksLocked =>
            "El servidor de esta instancia protege sus paquetes de recursos y de shaders.",
        ContentType.Modpack or ContentType.Server => "Los modpacks se instalan como una instancia nueva.",
        _ => null,
    };

    /// <summary>Mundos de la instancia (carpetas de <c>saves</c> con <c>level.dat</c>), para los data packs.</summary>
    public static IReadOnlyList<string> Worlds(string instanceDir)
    {
        var saves = Path.Combine(instanceDir, "saves");
        if (!Directory.Exists(saves))
            return [];
        return Directory.EnumerateDirectories(saves)
            .Where(d => File.Exists(Path.Combine(d, "level.dat")))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Índice de lo instalado ──────────────────────────────────────────────

    public sealed record IndexEntry(string Type, string Path, string VersionId);

    public static Dictionary<string, IndexEntry> LoadIndex(string instanceDir)
    {
        try
        {
            var json = File.ReadAllText(System.IO.Path.Combine(instanceDir, IndexFileName));
            return JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(json, ModrinthClient.Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Proyectos instalados desde Modrinth cuyo archivo sigue en la instancia.</summary>
    public static HashSet<string> InstalledProjects(string instanceDir) =>
        LoadIndex(instanceDir)
            .Where(e => File.Exists(System.IO.Path.Combine(instanceDir, e.Value.Path)))
            .Select(e => e.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static void SaveIndex(string instanceDir, Dictionary<string, IndexEntry> index)
    {
        var path = System.IO.Path.Combine(instanceDir, IndexFileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(index, ModrinthClient.Json));
        File.Move(tmp, path, overwrite: true);
    }

    // ── Mods, paquetes de recursos, shaders y data packs ────────────────────

    /// <summary>
    /// Instala la versión más reciente compatible con la instancia (preferiblemente estable) y, si es un
    /// mod, sus dependencias obligatorias. Si el proyecto ya estaba instalado en otra versión, la
    /// sustituye. <paramref name="world"/> es el mundo de destino de un data pack. Con
    /// <paramref name="versionId"/> se instala esa versión concreta (la elegida en la página del proyecto).
    /// </summary>
    public async Task<InstallResult> InstallAsync(GameInstance instance, string projectId, ContentType type, string? world = null,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default, string? versionId = null)
    {
        if (BlockReason(instance, type) is { } reason)
            throw new ModrinthException(reason);

        var instanceDir = _store.GetDirectory(instance.Id);
        var folder = TargetFolder(instanceDir, type, world);
        var index = LoadIndex(instanceDir);
        var dependencies = new List<string>();
        var missing = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        progress?.Report(new InstallProgress("Buscando una versión compatible…", null));
        var version = versionId is not null
            ? await _client.GetVersionAsync(versionId, ct).ConfigureAwait(false)
            : await PickVersionAsync(projectId, instance, type, ct).ConfigureAwait(false)
              ?? throw new ModrinthException(NoVersionText(instance, type));
        if (version.ProjectId != projectId)
            throw new ModrinthException("La versión elegida no es de este proyecto.");
        var file = await InstallVersionAsync(version, instanceDir, folder, type, index, progress, ct).ConfigureAwait(false);
        visited.Add(projectId);

        if (type == ContentType.Mod)
            await InstallDependenciesAsync(version, instance, instanceDir, folder, index, visited, dependencies, missing, 1, progress, ct).ConfigureAwait(false);

        SaveIndex(instanceDir, index);
        _logger.LogInformation("Instalado {Project} ({File}) en {Instance}; dependencias: {Deps}", projectId, file, instance.Id, dependencies.Count);
        return new InstallResult(file, dependencies, missing);
    }

    private static string TargetFolder(string instanceDir, ContentType type, string? world)
    {
        if (type == ContentType.DataPack)
        {
            if (string.IsNullOrWhiteSpace(world) || !IsSafeFileName(world))
                throw new ModrinthException("Elige el mundo en el que instalar el data pack.");
            var worldDir = Path.Combine(instanceDir, "saves", world);
            if (!Directory.Exists(worldDir))
                throw new ModrinthException($"No existe el mundo «{world}».");
            return Path.Combine(worldDir, "datapacks");
        }

        return Path.Combine(instanceDir, type.Folder() ?? throw new ModrinthException("Este contenido no se instala en una instancia."));
    }

    private static string NoVersionText(GameInstance instance, ContentType type)
    {
        var minecraft = GameVersion(instance) ?? "esta versión";
        return type == ContentType.Mod
            ? $"No hay ninguna versión para Minecraft {minecraft} con {GameInstance.LoaderDisplayName(instance.Display.Loader)}."
            : $"No hay ninguna versión para Minecraft {minecraft}.";
    }

    private async Task<ProjectVersion?> PickVersionAsync(string projectId, GameInstance instance, ContentType type, CancellationToken ct)
    {
        var gameVersion = GameVersion(instance);
        var versions = await _client.GetVersionsAsync(projectId, CompatibleLoaders(instance, type),
            gameVersion is null ? null : [gameVersion], ct).ConfigureAwait(false);
        return Pick(versions);
    }

    /// <summary>La más reciente estable con archivo; si no hay estables, la más reciente con archivo.</summary>
    public static ProjectVersion? Pick(IEnumerable<ProjectVersion> versions)
    {
        var withFile = versions.Where(v => v.PrimaryFile is not null).ToList();
        return withFile.FirstOrDefault(v => v.VersionType == "release") ?? withFile.FirstOrDefault();
    }

    private async Task<string> InstallVersionAsync(ProjectVersion version, string instanceDir, string folder, ContentType type,
        Dictionary<string, IndexEntry> index, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var file = version.PrimaryFile ?? throw new ModrinthException("La versión no tiene archivo para descargar.");
        if (!IsSafeFileName(file.Filename))
            throw new ModrinthException($"Nombre de archivo no válido: {file.Filename}");

        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, file.Filename);
        progress?.Report(new InstallProgress($"Descargando {file.Filename}…", 0));
        await DownloadAsync(file.Url, file.Hashes, file.Size, destination,
            new Progress<double>(p => progress?.Report(new InstallProgress($"Descargando {file.Filename}…", p))), ct).ConfigureAwait(false);

        // Otra versión del mismo proyecto instalada antes desde aquí: se sustituye.
        var relative = Path.GetRelativePath(instanceDir, destination).Replace('\\', '/');
        if (index.TryGetValue(version.ProjectId, out var previous) && !string.Equals(previous.Path, relative, StringComparison.OrdinalIgnoreCase))
        {
            var old = Path.GetFullPath(Path.Combine(instanceDir, previous.Path));
            if (IsInside(old, instanceDir) && File.Exists(old))
                File.Delete(old);
        }

        index[version.ProjectId] = new IndexEntry(type.ProjectType(), relative, version.Id);
        return file.Filename;
    }

    private async Task InstallDependenciesAsync(ProjectVersion version, GameInstance instance, string instanceDir, string folder,
        Dictionary<string, IndexEntry> index, HashSet<string> visited, List<string> added, List<string> missing, int depth,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (depth > MaxDependencyDepth)
            return;
        foreach (var dependency in version.Dependencies.Where(d => d.DependencyType == "required"))
        {
            ProjectVersion? resolved;
            if (dependency.VersionId is { Length: > 0 } versionId)
            {
                resolved = await _client.GetVersionAsync(versionId, ct).ConfigureAwait(false);
            }
            else if (dependency.ProjectId is { Length: > 0 } projectId)
            {
                if (visited.Contains(projectId) || InstalledHere(index, instanceDir, projectId))
                    continue;
                resolved = await PickVersionAsync(projectId, instance, ContentType.Mod, ct).ConfigureAwait(false);
            }
            else
            {
                continue; // dependencia de un archivo externo: no se puede resolver
            }

            if (resolved is null)
            {
                missing.Add(dependency.ProjectId ?? dependency.VersionId ?? "?");
                continue;
            }

            if (!visited.Add(resolved.ProjectId) || InstalledHere(index, instanceDir, resolved.ProjectId))
                continue;
            progress?.Report(new InstallProgress("Instalando dependencias…", null));
            added.Add(await InstallVersionAsync(resolved, instanceDir, folder, ContentType.Mod, index, progress, ct).ConfigureAwait(false));
            await InstallDependenciesAsync(resolved, instance, instanceDir, folder, index, visited, added, missing, depth + 1, progress, ct).ConfigureAwait(false);
        }
    }

    private static bool InstalledHere(Dictionary<string, IndexEntry> index, string instanceDir, string projectId) =>
        index.TryGetValue(projectId, out var entry) && File.Exists(Path.Combine(instanceDir, entry.Path));

    // ── Modpacks (.mrpack) ──────────────────────────────────────────────────

    /// <summary>
    /// Crea una instancia con el modpack: su versión de Minecraft y su cargador, los archivos de su
    /// índice (los que no son solo de servidor) y sus <c>overrides</c>. Si algo falla, la instancia
    /// a medias se borra. Con <paramref name="loaders"/> o <paramref name="gameVersions"/> (los filtros
    /// activos) se prefiere una versión que los cumpla; si no hay ninguna, la más reciente.
    /// </summary>
    public async Task<GameInstance> InstallModpackAsync(string projectId, string title, IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default, IReadOnlyCollection<string>? loaders = null, IReadOnlyCollection<string>? gameVersions = null,
        string? versionId = null)
    {
        progress?.Report(new InstallProgress("Buscando la última versión del modpack…", null));
        static bool IsPack(ProjectVersion v) => v.PrimaryFile?.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) == true;
        var versions = await _client.GetVersionsAsync(projectId, ct: ct).ConfigureAwait(false);
        if (versionId is not null)
            versions = versions.Where(v => v.Id == versionId).ToList();
        var version = Pick(versions.Where(v => IsPack(v)
                                               && (loaders is not { Count: > 0 } || v.Loaders.Intersect(loaders, StringComparer.OrdinalIgnoreCase).Any())
                                               && (gameVersions is not { Count: > 0 } || v.GameVersions.Intersect(gameVersions).Any())))
                      ?? Pick(versions.Where(IsPack))
                      ?? throw new ModrinthException("Este modpack no tiene ninguna versión para descargar.");
        var file = version.PrimaryFile!;

        var temp = Path.Combine(Path.GetTempPath(), $"konnor-modpack-{Guid.NewGuid():N}.mrpack");
        GameInstance? created = null;
        try
        {
            await DownloadAsync(file.Url, file.Hashes, file.Size, temp,
                new Progress<double>(p => progress?.Report(new InstallProgress("Descargando el modpack…", p))), ct).ConfigureAwait(false);

            using var archive = ZipFile.OpenRead(temp);
            var pack = MrpackIndex.Read(archive);
            created = _store.Create(new GameInstance
            {
                Name = UniqueName(title),
                MinecraftVersion = pack.Minecraft,
                Loader = pack.Loader,
                LoaderChannel = pack.Loader == ModLoader.Vanilla ? LoaderChannel.Stable : LoaderChannel.Specific,
                LoaderVersion = pack.LoaderVersion,
            });
            var instanceDir = _store.GetDirectory(created.Id);

            await DownloadPackFilesAsync(pack.Files, instanceDir, progress, ct).ConfigureAwait(false);
            progress?.Report(new InstallProgress("Copiando la configuración del modpack…", null));
            ExtractOverrides(archive, instanceDir, ct);
            _logger.LogInformation("Modpack {Project} instalado como {Instance} ({Files} archivos)", projectId, created.Id, pack.Files.Count);
            return created;
        }
        catch
        {
            if (created is not null)
                TryDeleteInstance(created.Id);
            throw;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string UniqueName(string title)
    {
        var baseName = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (baseName.Length == 0)
            baseName = "Modpack";
        if (baseName.Length > InstanceStore.MaxNameLength - 4)
            baseName = baseName[..(InstanceStore.MaxNameLength - 4)].TrimEnd();
        var names = _store.LoadAll().Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = baseName;
        for (var n = 2; names.Contains(name); n++)
            name = $"{baseName} ({n})";
        return name;
    }

    private async Task DownloadPackFilesAsync(IReadOnlyList<MrpackFile> files, string instanceDir, IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var total = Math.Max(1, files.Sum(f => Math.Max(0, f.Size)));
        long done = 0;
        var finished = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = ParallelDownloads, CancellationToken = ct },
            async (file, token) =>
            {
                var destination = SafePackPath(instanceDir, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await DownloadAsync(file.Url, file.Hashes, file.Size, destination, null, token).ConfigureAwait(false);
                var bytes = Interlocked.Add(ref done, Math.Max(0, file.Size));
                var count = Interlocked.Increment(ref finished);
                progress?.Report(new InstallProgress($"Descargando mods ({count} de {files.Count})…", bytes * 100.0 / total));
            }).ConfigureAwait(false);
    }

    /// <summary><c>overrides/</c> y después <c>client-overrides/</c> (tiene prioridad) se copian a la instancia.</summary>
    private static void ExtractOverrides(ZipArchive archive, string instanceDir, CancellationToken ct)
    {
        foreach (var prefix in new[] { "overrides/", "client-overrides/" })
        {
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/') || name.Length == prefix.Length)
                    continue;
                if (entry.Length > MaxUnknownFileBytes)
                    throw new ModrinthException($"El modpack contiene un archivo demasiado grande: {name}");
                var destination = SafePackPath(instanceDir, name[prefix.Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Ruta dentro de la instancia para una ruta del modpack. Rechaza rutas absolutas, con <c>..</c>, que
    /// salgan de la instancia o que sustituyan los archivos del launcher.
    /// </summary>
    public static string SafePackPath(string instanceDir, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) || normalized.Contains(':')
            || segments.Any(s => s is "" or "." or ".."))
            throw new ModrinthException($"El modpack contiene una ruta no válida: {relative}");

        var root = Path.GetFullPath(instanceDir);
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!IsInside(full, root))
            throw new ModrinthException($"El modpack contiene una ruta no válida: {relative}");
        if (segments.Length == 1 && (segments[0].Equals(InstanceStore.FileName, StringComparison.OrdinalIgnoreCase)
                                     || segments[0].Equals(IndexFileName, StringComparison.OrdinalIgnoreCase)))
            throw new ModrinthException($"El modpack intenta sustituir un archivo del launcher: {relative}");
        return full;
    }

    private static bool IsInside(string path, string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDeleteInstance(string id)
    {
        try
        {
            _store.Delete(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo borrar la instancia a medias {Id}", id);
        }
    }

    // ── Descargas ───────────────────────────────────────────────────────────

    /// <summary>Solo HTTPS (o HTTP a este mismo equipo, para las pruebas).</summary>
    public static bool IsAllowedUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;
        uri = parsed;
        return parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback);
    }

    /// <summary>Un nombre de archivo sin carpetas ni caracteres que Windows no admite.</summary>
    public static bool IsSafeFileName(string name) =>
        name.Length is > 0 and <= 200
        && name is not "." and not ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && !name.EndsWith('.') && !name.EndsWith(' ');

    /// <summary>
    /// Descarga a un temporal junto al destino, comprobando el tamaño y el hash, y lo mueve a su sitio.
    /// Solo HTTPS (o HTTP a este mismo equipo, para las pruebas).
    /// </summary>
    internal async Task DownloadAsync(string url, FileHashes hashes, long size, string destination, IProgress<double>? progress,
        CancellationToken ct)
    {
        if (!IsAllowedUrl(url, out var uri))
            throw new ModrinthException("Dirección de descarga no válida.");
        var (algorithm, expected) = hashes.Sha512 is { Length: > 0 } sha512
            ? (HashAlgorithmName.SHA512, sha512)
            : hashes.Sha1 is { Length: > 0 } sha1
                ? (HashAlgorithmName.SHA1, sha1)
                : throw new ModrinthException("El archivo no tiene hash para comprobarlo.");
        var limit = size > 0 ? size : MaxUnknownFileBytes;

        var tmp = destination + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(algorithm);
            using (var response = await _client.Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new ModrinthException($"No se pudo descargar el archivo ({(int)response.StatusCode}).");
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > limit)
                        throw new ModrinthException("El archivo es más grande de lo anunciado; se descarta.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (size > 0)
                        progress?.Report(written * 100.0 / size);
                }
            }

            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase))
                throw new ModrinthException("El archivo descargado no coincide con su hash; se descarta.");
            File.Move(tmp, destination, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            throw new ModrinthException("No se pudo descargar el archivo. Comprueba la conexión a Internet.", ex);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}

/// <summary>Archivo del índice de un modpack.</summary>
public sealed record MrpackFile(string Path, string Url, FileHashes Hashes, long Size);

/// <summary><c>modrinth.index.json</c> de un modpack: versión de Minecraft, cargador y archivos del cliente.</summary>
public sealed record MrpackIndex(string Minecraft, ModLoader Loader, string? LoaderVersion, IReadOnlyList<MrpackFile> Files)
{
    public static MrpackIndex Read(ZipArchive archive)
    {
        var entry = archive.GetEntry("modrinth.index.json") ?? throw new ModrinthException("El archivo no es un modpack de Modrinth.");
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        return Parse(document.RootElement);
    }

    public static MrpackIndex Parse(JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object
            || !deps.TryGetProperty("minecraft", out var mc) || mc.GetString() is not { Length: > 0 } minecraft)
            throw new ModrinthException("El modpack no indica su versión de Minecraft.");

        var (loader, loaderVersion) = (ModLoader.Vanilla, (string?)null);
        foreach (var (key, value) in new[] { ("fabric-loader", ModLoader.Fabric), ("quilt-loader", ModLoader.Quilt), ("forge", ModLoader.Forge), ("neoforge", ModLoader.NeoForge) })
        {
            if (deps.TryGetProperty(key, out var version) && version.GetString() is { Length: > 0 } v)
            {
                (loader, loaderVersion) = (value, v);
                break;
            }
        }

        var files = new List<MrpackFile>();
        if (root.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in list.EnumerateArray())
            {
                // Los archivos solo de servidor no se descargan.
                if (file.TryGetProperty("env", out var env) && env.TryGetProperty("client", out var client) && client.GetString() == "unsupported")
                    continue;
                var path = file.GetProperty("path").GetString() ?? string.Empty;
                var url = file.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array
                    ? downloads.EnumerateArray().Select(d => d.GetString()).FirstOrDefault(u => ContentInstaller.IsAllowedUrl(u, out _))
                    : null;
                if (url is null)
                    throw new ModrinthException($"El modpack no tiene una descarga segura para {path}.");
                var hashes = file.GetProperty("hashes");
                files.Add(new MrpackFile(path, url,
                    new FileHashes
                    {
                        Sha1 = hashes.TryGetProperty("sha1", out var s1) ? s1.GetString() : null,
                        Sha512 = hashes.TryGetProperty("sha512", out var s5) ? s5.GetString() : null,
                    },
                    file.TryGetProperty("fileSize", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0));
            }
        }

        return new MrpackIndex(minecraft, loader, loaderVersion, files);
    }
}
