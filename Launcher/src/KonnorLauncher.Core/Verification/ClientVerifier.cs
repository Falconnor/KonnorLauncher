using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Progress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Verification;

/// <summary>Parámetros de una verificación (se construyen desde <c>launcher.properties</c>).</summary>
public sealed record VerifierOptions
{
    /// <summary>Carpeta del juego ya resuelta (absoluta).</summary>
    public required string GameDir { get; init; }

    /// <summary>Valor de <c>server.url</c> (se le quita la <c>/</c> final).</summary>
    public required string ServerUrl { get; init; }

    /// <summary><c>enable.verify</c>: si es false la verificación termina con éxito sin red.</summary>
    public bool EnableVerify { get; init; }

    /// <summary>
    /// Limpieza de archivos no listados si el manifest no dice nada. Si el servidor publica
    /// <c>delete_unauthorized</c>, manda el servidor.
    /// </summary>
    public bool DeleteUnauthorized { get; init; }

    /// <summary>Modo simulación (<c>--dry-run</c>): no descarga, no reemplaza, no borra.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Si la carpeta del juego es la <c>.minecraft</c> oficial, la limpieza no se ejecuta, para no borrar
    /// los mods ni las versiones del launcher de Mojang. En false, se limpia igualmente.
    /// </summary>
    public bool SkipCleanupInOfficialDir { get; init; } = true;

    /// <summary>
    /// El jugador quiere los paquetes de recursos y de shaders del servidor. Si el servidor no los
    /// protege (<c>protect_packs</c>), solo se instalan los que no tenga ya con el mismo nombre y nunca
    /// se reparan ni se borran. Si los protege, se instalan siempre, sea cual sea este valor.
    /// </summary>
    public bool InstallPacks { get; init; } = true;

    /// <summary>Descargas simultáneas de archivos sueltos (1 = una detrás de otra).</summary>
    public int MaxParallelDownloads { get; init; } = 4;

    /// <summary>Bloques de assets que se descargan a la vez.</summary>
    public int MaxParallelBlockDownloads { get; init; } = 2;

    /// <summary>Bloques de assets que se extraen a la vez (cada uno en su propio hilo).</summary>
    public int MaxParallelExtractions { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
}

/// <summary>
/// Sincroniza el cliente con el servidor. Mantiene exactamente el mismo protocolo que la versión
/// en Python (endpoints, formato del manifest y textos de estado), así el servidor no necesita cambios.
/// <para>
/// Las descargas se reanudan: cada una se guarda en <see cref="DownloadsFolderName"/> con el hash
/// esperado en el nombre. Si se corta (red, cierre del launcher...), el siguiente intento pide al
/// servidor solo lo que falta con una cabecera <c>Range</c>.
/// </para>
/// </summary>
public sealed partial class ClientVerifier
{
    /// <summary>Las únicas carpetas que la limpieza puede tocar (nunca saves, config, options.txt...).</summary>
    public static readonly IReadOnlyList<string> ProtectedFolders = ["mods", "versions", "libraries", "assets"];

    /// <summary>
    /// Carpetas del juego. Si el manifest publica la versión (<c>minecraft</c>), las llena el propio
    /// launcher desde Mojang y el cargador, no el servidor: la limpieza no las toca.
    /// </summary>
    public static readonly IReadOnlyList<string> GameFolders = ["versions", "libraries", "assets"];

    /// <summary>
    /// Paquetes de recursos y de shaders. Solo se protegen (reparación y limpieza) si el servidor
    /// publica <c>protect_packs</c>; si no, son del jugador.
    /// </summary>
    public static readonly IReadOnlyList<string> PackFolders = ["resourcepacks", "shaderpacks"];

    /// <summary>
    /// Carpeta (dentro de la del juego) con las descargas en curso. No está entre las protegidas, así
    /// que la limpieza no la toca; se borra al terminar una verificación con éxito.
    /// </summary>
    public const string DownloadsFolderName = ".konnor-descargas";

    private const int BufferSize = 1024 * 1024;
    private const int MaxAttempts = 3;
    private const double BytesPerMb = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <param name="http">
    /// Cliente HTTP. Debe crearse con <c>Timeout = Timeout.InfiniteTimeSpan</c>: los timeouts se controlan
    /// por petición y por lectura, y el global (100 s por defecto) cortaría descargas grandes.
    /// </param>
    public ClientVerifier(HttpClient http, ILogger<ClientVerifier>? logger = null)
    {
        _http = http;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Timeout para descargar el manifest.</summary>
    public TimeSpan ManifestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Timeout de conexión/lectura de cada descarga (no es un límite total).</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Espera base entre reintentos (exponencial: 1×, 2×...).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Se invoca con <c>client_version</c> cuando el manifest la trae (para <c>cached.client.version</c>).</summary>
    public Action<string>? ClientVersionReceived { get; set; }

    /// <summary>Se invoca con el manifest ya validado (la instancia guarda la política del servidor).</summary>
    public Action<ClientManifest>? ManifestReceived { get; set; }

    /// <summary>
    /// Réplica de los archivos que llevan SHA-512 (Modrinth): recibe esos hashes y devuelve, de los que
    /// encuentra, la dirección de descarga. Solo se usa para descargar; lo descargado se comprueba con
    /// el SHA-256 del manifest, y si no está o falla se descarga del servidor. null = solo el servidor.
    /// </summary>
    public Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>>? MirrorLookup { get; set; }

    // ══════════════════════════════════════════════════════════════════════
    // FLUJO PRINCIPAL
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ejecuta la verificación. Solo lanza excepciones al cancelar (<see cref="OperationCanceledException"/>);
    /// cualquier fallo se devuelve como <see cref="VerificationResult"/> con <c>Success = false</c>.
    /// La limpieza solo se ejecuta si TODAS las descargas y hashes fueron correctos.
    /// </summary>
    public async Task<VerificationResult> VerifyAsync(VerifierOptions options, IProgress<LauncherProgress>? progress = null, CancellationToken ct = default)
    {
        void Status(string text, double? percent = null, LauncherPhase phase = LauncherPhase.Verifying)
        {
            progress?.Report(new LauncherProgress(phase, text, percent));
        }

        // 1. Verificación desactivada → éxito inmediato, sin peticiones de red.
        if (!options.EnableVerify)
        {
            Status(VerificationResult.SuccessStatus);
            return new VerificationResult(true, VerificationResult.SuccessStatus);
        }

        string? downloadsDir = null;
        var succeeded = false;
        try
        {
            // 2. Carpeta del juego y URL base.
            var gameDir = Path.GetFullPath(options.GameDir);
            Directory.CreateDirectory(gameDir);
            var baseUrl = options.ServerUrl.Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
                throw new VerificationException("server.url no está configurado");
            RemoveStaleTempDirs(gameDir);

            // 3. Manifest.
            var manifest = await DownloadManifestAsync($"{baseUrl}/client_manifest.json", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(manifest.ClientVersion))
                ClientVersionReceived?.Invoke(manifest.ClientVersion!);
            ManifestReceived?.Invoke(manifest);

            // 4-5. Qué hay que reparar (existencia + tamaño; el hash solo valida lo descargado).
            var protectPacks = manifest.ProtectPacks == true;
            var files = SelectFiles(gameDir, manifest.Files, protectPacks, options.InstallPacks, _logger);
            var repairBlocks = FindBlocksToRepair(gameDir, manifest.AssetBlocks);
            var repairFiles = FindFilesToRepair(gameDir, files);
            _logger.LogInformation("Manifest: {Files} archivos, {Blocks} bloques. A reparar: {RFiles} archivos, {RBlocks} bloques",
                manifest.Files.Count, manifest.AssetBlocks.Count, repairFiles.Count, repairBlocks.Count);

            // 6. Simulación: informa sin tocar nada.
            if (options.DryRun)
            {
                Status($"Simulación: {repairBlocks.Count} bloques y {repairFiles.Count} archivos requieren reparación");
                Status(VerificationResult.SuccessStatus);
                return new VerificationResult(true, VerificationResult.SuccessStatus, repairBlocks.Count, repairFiles.Count);
            }

            // 7. Carpeta de descargas dentro del juego (mismo volumen → mover es atómico).
            if (repairBlocks.Count + repairFiles.Count > 0)
            {
                downloadsDir = Path.Combine(gameDir, DownloadsFolderName);
                var info = Directory.CreateDirectory(downloadsDir);
                info.Attributes |= FileAttributes.Hidden;
            }

            var totalBytes = repairBlocks.Sum(b => b.Size) + repairFiles.Sum(f => f.Size);
            var tracker = new DownloadTracker(totalBytes, (current, total) =>
                Status(FormatDownloadStatus(current, total), total > 0 ? Math.Min(100, current * 100.0 / total) : 0, LauncherPhase.Downloading));

            // 8. Fase 1: bloques. Varios se descargan a la vez y otros tantos se extraen en paralelo.
            await RepairBlocksAsync(baseUrl, gameDir, downloadsDir, repairBlocks, tracker, options, ct).ConfigureAwait(false);

            // 9. Fase 2: archivos sueltos con paralelismo limitado.
            await RepairFilesAsync(baseUrl, gameDir, downloadsDir, repairFiles, tracker, options.MaxParallelDownloads, ct).ConfigureAwait(false);

            // 10. Limpieza (solo si todo lo anterior tuvo éxito). La decide el servidor si la publica.
            var removed = 0;
            var deleteUnauthorized = manifest.DeleteUnauthorized ?? options.DeleteUnauthorized;
            if (deleteUnauthorized)
            {
                if (options.SkipCleanupInOfficialDir && LauncherPaths.IsOfficialMinecraftDir(gameDir))
                {
                    _logger.LogWarning("Limpieza omitida: la carpeta del juego es la .minecraft oficial ({GameDir})", gameDir);
                }
                else
                {
                    var allowed = manifest.Files.Concat(manifest.AssetBlocks.SelectMany(b => b.Files))
                        .Select(f => ManifestPath.ComparisonKey(f.RelativePath))
                        .ToHashSet(StringComparer.Ordinal);
                    var folders = manifest.Game is null ? ProtectedFolders : ProtectedFolders.Except(GameFolders);
                    if (protectPacks)
                        folders = folders.Concat(PackFolders);
                    removed = RemoveUnauthorizedFiles(gameDir, allowed, folders);
                    _logger.LogInformation("Limpieza: {Removed} archivos no autorizados eliminados", removed);
                }
            }

            // 11. Fin.
            succeeded = true;
            Status(VerificationResult.SuccessStatus);
            return new VerificationResult(true, VerificationResult.SuccessStatus, repairBlocks.Count, repairFiles.Count, removed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Verificación cancelada; las descargas a medias se reanudarán la próxima vez");
            throw;
        }
        catch (Exception ex)
        {
            // Cualquier fallo se trata igual (también un ZIP corrupto o una entrada que falta).
            // La UI solo distingue "servidor no responde" del resto.
            var status = ex.Message == VerificationException.ServerNotResponding
                ? VerificationException.ServerNotResponding
                : VerificationResult.FailedStatus;
            _logger.LogError(ex, "Verificación fallida: {Message}", ex.Message);
            Status(status);
            return new VerificationResult(false, status);
        }
        finally
        {
            // Con éxito, la carpeta de descargas sobra. Con error o cancelación se conserva lo que
            // tenga dentro para reanudar, salvo que esté vacía.
            if (downloadsDir is not null)
            {
                if (succeeded)
                    TryDeleteDirectory(downloadsDir);
                else
                    TryDeleteIfEmpty(downloadsDir);
            }
        }
    }

    /// <summary>Texto visible durante las descargas (siempre con punto decimal).</summary>
    public static string FormatDownloadStatus(long current, long total) =>
        string.Create(CultureInfo.InvariantCulture, $"descargando archivos: {current / BytesPerMb:F2} MB / {total / BytesPerMb:F2} MB");

    // ══════════════════════════════════════════════════════════════════════
    // QUÉ HAY QUE REPARAR
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pack al que pertenece una ruta del manifest (<c>resourcepacks/Nombre.zip</c> o la carpeta
    /// <c>shaderpacks/Nombre</c>), o null si no está en <see cref="PackFolders"/>.
    /// </summary>
    public static (string Folder, string Name)? PackOf(string urlPath)
    {
        var parts = urlPath.Split('/');
        if (parts.Length < 2)
            return null;
        var folder = PackFolders.FirstOrDefault(f => string.Equals(f, parts[0], StringComparison.OrdinalIgnoreCase));
        return folder is null ? null : (parts[0], parts[1]);
    }

    /// <summary>
    /// Archivos del manifest que hay que tener en disco. Los de los packs dependen de la política:
    /// <list type="bullet">
    /// <item>el servidor los protege: todos, como el resto;</item>
    /// <item>no los protege y el jugador no los quiere: ninguno;</item>
    /// <item>no los protege y el jugador los quiere: solo los de packs que no tenga ya con el mismo
    /// nombre (archivo o carpeta). Los suyos nunca se reemplazan.</item>
    /// </list>
    /// </summary>
    public static List<ManifestFile> SelectFiles(string gameDir, IEnumerable<ManifestFile> files, bool protectPacks, bool installPacks,
        ILogger? logger = null)
    {
        var installed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<ManifestFile>();
        foreach (var file in files)
        {
            if (protectPacks || PackOf(file.UrlPath) is not { } pack)
            {
                selected.Add(file);
                continue;
            }

            if (!installPacks)
                continue;
            var key = pack.Folder + "/" + pack.Name;
            if (!installed.TryGetValue(key, out var exists))
            {
                var local = Path.Combine(gameDir, pack.Folder, pack.Name);
                exists = File.Exists(local) || Directory.Exists(local);
                installed[key] = exists;
                if (exists)
                    logger?.LogInformation("Pack {Pack} ya instalado: no se descarga", key);
            }

            if (!exists)
                selected.Add(file);
        }

        return selected;
    }

    /// <summary>Archivos ausentes o con tamaño distinto. Si existe con el mismo tamaño se da por válido (evita hashear miles de JAR).</summary>
    public static List<ManifestFile> FindFilesToRepair(string gameDir, IEnumerable<ManifestFile> files) =>
        files.Where(f => !HasExpectedSize(ManifestPath.ToLocalPath(gameDir, f), f.Size)).ToList();

    /// <summary>Un bloque entero se repara si CUALQUIERA de sus archivos falta o tiene tamaño distinto.</summary>
    public static List<AssetBlock> FindBlocksToRepair(string gameDir, IEnumerable<AssetBlock> blocks) =>
        blocks.Where(b => b.Files.Any(f => !HasExpectedSize(ManifestPath.ToLocalPath(gameDir, f), f.Size))).ToList();

    private static bool HasExpectedSize(string path, long size)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // FASE 1: BLOQUES DE ASSETS (descarga y extracción en paralelo)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tubería de dos etapas unidas por una cola: <see cref="VerifierOptions.MaxParallelBlockDownloads"/>
    /// descargas alimentan a <see cref="VerifierOptions.MaxParallelExtractions"/> extractores. Mientras
    /// unos bloques se extraen, los siguientes ya se están descargando. La cola es acotada para que
    /// las descargas no se adelanten demasiado (cada bloque ocupa decenas de MB en disco).
    /// Si cualquier etapa falla, se cancela el resto y se propaga ese primer error.
    /// </summary>
    private async Task RepairBlocksAsync(string baseUrl, string gameDir, string? downloadsDir, List<AssetBlock> blocks,
        DownloadTracker tracker, VerifierOptions options, CancellationToken ct)
    {
        if (blocks.Count == 0 || downloadsDir is null)
            return;

        using var pipeline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? firstError = null;
        void Fail(Exception ex)
        {
            // Las cancelaciones provocadas por otro fallo no son el error a informar.
            if (ex is OperationCanceledException && pipeline.IsCancellationRequested)
                return;
            Interlocked.CompareExchange(ref firstError, ex, null);
            pipeline.Cancel();
        }

        var extractors = Math.Max(1, options.MaxParallelExtractions);
        var queue = Channel.CreateBounded<(AssetBlock Block, string ZipPath)>(new BoundedChannelOptions(extractors));

        var extraction = Enumerable.Range(0, extractors).Select(_ => Task.Run(async () =>
        {
            try
            {
                await foreach (var (block, zipPath) in queue.Reader.ReadAllAsync(pipeline.Token).ConfigureAwait(false))
                    ExtractBlock(zipPath, gameDir, block, pipeline.Token);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        })).ToArray();

        try
        {
            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelBlockDownloads),
                CancellationToken = pipeline.Token,
            };
            await Parallel.ForEachAsync(blocks, parallel, async (block, token) =>
            {
                var url = $"{baseUrl}/client/packages/{ManifestPath.EncodeUrlPath(block.Name)}";
                var zipPath = await DownloadAsync(url, downloadsDir, block.Name, block.Sha256, block.Size,
                    Path.GetFileName(block.Name), tracker, token).ConfigureAwait(false);
                await queue.Writer.WriteAsync((block, zipPath), token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            queue.Writer.TryComplete();
        }

        await Task.WhenAll(extraction).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (firstError is not null)
            ExceptionDispatchInfo.Throw(firstError);
    }

    /// <summary>
    /// Extrae SOLO las entradas listadas en el manifest y después borra el ZIP.
    /// Se escribe directamente en el destino: si algo se corta a medias, el archivo queda con un
    /// tamaño distinto y la siguiente verificación lo vuelve a reparar.
    /// </summary>
    private void ExtractBlock(string zipPath, string gameDir, AssetBlock block, CancellationToken ct)
    {
        _logger.LogDebug("extrayendo {Block}", block.Name);
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var file in block.Files)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(file.UrlPath)
                    ?? throw new VerificationException($"El bloque {block.Name} no contiene {file.UrlPath}");
                var destination = ManifestPath.ToLocalPath(gameDir, file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);

                var length = new FileInfo(destination).Length;
                if (length != file.Size)
                {
                    // Se avisa en el log, pero no bloquea el juego.
                    _logger.LogWarning("Tamaño extraído distinto al manifest: {Path} ({Actual} != {Expected})", file.UrlPath, length, file.Size);
                }
            }
        }

        File.Delete(zipPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    // FASE 2: ARCHIVOS SUELTOS
    // ══════════════════════════════════════════════════════════════════════

    private async Task RepairFilesAsync(string baseUrl, string gameDir, string? downloadsDir, List<ManifestFile> files,
        DownloadTracker tracker, int maxParallel, CancellationToken ct)
    {
        if (files.Count == 0 || downloadsDir is null)
            return;

        var mirrors = await FindMirrorsAsync(files, ct).ConfigureAwait(false);
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallel), CancellationToken = ct };
        await Parallel.ForEachAsync(files, parallelOptions, async (file, token) =>
        {
            // Descarga (o reanuda) en la carpeta de descargas, verificando el hash: primero de Modrinth
            // si lo tiene y, si no, o si allí falla, del servidor. El parcial es el mismo en los dos casos
            // (mismo contenido), así que lo que se bajó de uno se reanuda desde el otro.
            var url = $"{baseUrl}/client/{ManifestPath.EncodeUrlPath(file.UrlPath)}";
            var name = Path.GetFileName(file.RelativePath);
            string? downloaded = null;
            if (file.Sha512 is { } sha512 && mirrors.TryGetValue(sha512, out var mirror))
            {
                try
                {
                    downloaded = await DownloadAsync(mirror, downloadsDir, file.UrlPath, file.Sha256, file.Size, name, tracker, token)
                        .ConfigureAwait(false);
                }
                catch (VerificationException ex)
                {
                    _logger.LogWarning(ex, "No se pudo descargar {File} de Modrinth; se descarga del servidor", name);
                }
            }

            downloaded ??= await DownloadAsync(url, downloadsDir, file.UrlPath, file.Sha256, file.Size,
                name, tracker, token).ConfigureAwait(false);

            // ...y se mueve a su destino de forma atómica.
            var destination = ManifestPath.ToLocalPath(gameDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(downloaded, destination, overwrite: true);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Direcciones en la réplica (Modrinth) de los archivos a reparar que llevan SHA-512. Si la réplica no
    /// responde, todo se descarga del servidor: nunca bloquea la verificación.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> FindMirrorsAsync(List<ManifestFile> files, CancellationToken ct)
    {
        var hashes = files.Select(f => f.Sha512).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (MirrorLookup is null || hashes.Count == 0)
            return new Dictionary<string, string>();
        try
        {
            var found = await MirrorLookup(hashes, ct).ConfigureAwait(false);
            var mirrors = found
                .Where(m => Modrinth.ContentInstaller.IsAllowedUrl(m.Value, out _)) // HTTPS (o local, en pruebas)
                .ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation("Modrinth: {Found} de {Total} archivos a reparar se descargan de allí", mirrors.Count, hashes.Count);
            return mirrors;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "No se pudo consultar Modrinth; todo se descarga del servidor");
            return new Dictionary<string, string>();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // LIMPIEZA
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Borra, solo dentro de <paramref name="folders"/> (por defecto <see cref="ProtectedFolders"/>), todo
    /// archivo cuya ruta relativa no esté en <paramref name="allowedKeys"/> (claves de
    /// <see cref="ManifestPath.ComparisonKey"/>). No sigue enlaces simbólicos ni uniones, así que nunca
    /// sale de esas carpetas.
    /// </summary>
    public static int RemoveUnauthorizedFiles(string gameDir, IReadOnlySet<string> allowedKeys, IEnumerable<string>? folders = null)
    {
        var removed = 0;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var folder in folders ?? ProtectedFolders)
        {
            var folderPath = Path.Combine(gameDir, folder);
            if (!Directory.Exists(folderPath) || new DirectoryInfo(folderPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            foreach (var path in Directory.EnumerateFiles(folderPath, "*", enumeration).ToList())
            {
                var relative = Path.GetRelativePath(gameDir, path);
                if (allowedKeys.Contains(ManifestPath.ComparisonKey(relative)))
                    continue;
                File.SetAttributes(path, FileAttributes.Normal); // Para poder borrar archivos de solo lectura.
                File.Delete(path);
                removed++;
            }
        }

        return removed;
    }

    // ══════════════════════════════════════════════════════════════════════
    // RED: MANIFEST Y DESCARGAS REANUDABLES CON HASH
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Descarga y parsea el manifest. Cualquier fallo de red, HTTP, timeout o JSON → <c>servidor no responde</c>.</summary>
    private async Task<ClientManifest> DownloadManifestAsync(string url, CancellationToken ct)
    {
        string json;
        try
        {
            json = await WithRetriesAsync(async attemptCt =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attemptCt);
                timeout.CancelAfter(ManifestTimeout);
                try
                {
                    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!attemptCt.IsCancellationRequested)
                {
                    throw new TimeoutException("Timeout descargando el manifest", ex);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
        {
            throw new VerificationException(VerificationException.ServerNotResponding, ex);
        }

        return ManifestParser.Parse(json);
    }

    /// <summary>
    /// Nombre de la descarga parcial: hash esperado + identificador de la ruta. Así una descarga solo
    /// se reanuda si es exactamente el mismo contenido para el mismo destino (dos archivos idénticos
    /// en rutas distintas no comparten parcial).
    /// </summary>
    internal static string PartialFileName(string key, string expectedHash)
    {
        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        return $"{expectedHash.ToLowerInvariant()}-{keyHash}.part";
    }

    /// <summary>
    /// Descarga un archivo verificando su SHA-256 y devuelve la ruta del archivo completo.
    /// <list type="bullet">
    /// <item>Si ya hay una descarga parcial, pide solo lo que falta (<c>Range: bytes=N-</c>).</item>
    /// <item>Si el servidor ignora el <c>Range</c> (responde 200), empieza desde cero.</item>
    /// <item>Los errores de red transitorios se reintentan reanudando desde donde se cortó.</item>
    /// <item>Si el hash no coincide tras reanudar, el parcial estaba dañado: se descarta y se baja
    /// entero una vez más. Si tampoco coincide así, es un error.</item>
    /// </list>
    /// </summary>
    private async Task<string> DownloadAsync(string url, string downloadsDir, string key, string expectedHash, long expectedSize,
        string displayName, DownloadTracker tracker, CancellationToken ct)
    {
        var partPath = Path.Combine(downloadsDir, PartialFileName(key, expectedHash));
        long reported = 0;
        void Report(long totalForFile)
        {
            tracker.Add(totalForFile - reported);
            reported = totalForFile;
        }

        for (var pass = 1; ; pass++)
        {
            var resumed = false;
            string actualHash;
            try
            {
                actualHash = await WithRetriesAsync(async attemptCt =>
                {
                    var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                    if (existing > expectedSize)
                    {
                        File.Delete(partPath);
                        existing = 0;
                    }

                    resumed |= existing > 0;
                    Report(existing);
                    return await DownloadAttemptAsync(url, partPath, existing, expectedSize, displayName, Report, attemptCt).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
            {
                // Lo ya descargado se descuenta del progreso: si se reintenta desde otra dirección, se
                // vuelve a contar al reanudar.
                Report(0);
                throw new VerificationException($"No se pudo descargar {displayName}", ex);
            }

            if (string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                return partPath;

            // Contenido incorrecto: nunca se conserva.
            TryDeleteFile(partPath);
            Report(0);
            if (!resumed || pass >= 2)
                throw new VerificationException($"Hash inválido en la descarga: {displayName}");
            _logger.LogWarning("La descarga parcial de {File} estaba dañada; se descarga de nuevo completa", displayName);
        }
    }

    /// <summary>
    /// Un intento de descarga. Calcula el SHA-256 de todo el archivo: primero el de los bytes que ya
    /// había en disco y después el de los que llegan, mientras se escriben.
    /// </summary>
    private async Task<string> DownloadAttemptAsync(string url, string partPath, long existing, long expectedSize,
        string displayName, Action<long> report, CancellationToken attemptCt)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];

        // Bytes ya descargados en un intento o sesión anterior.
        if (existing > 0)
        {
            await using var previous = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            int count;
            while ((count = await previous.ReadAsync(buffer, attemptCt).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, count);
        }

        if (existing == expectedSize)
        {
            // Ya estaba completo. Un archivo de 0 bytes nunca llega a crearse al descargar, así que
            // se crea aquí para que exista al moverlo a su destino.
            if (!File.Exists(partPath))
                await File.WriteAllBytesAsync(partPath, [], attemptCt).ConfigureAwait(false);
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attemptCt);
        timeout.CancelAfter(DownloadTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // El parcial no encaja con el archivo del servidor: se descarta y se reintenta entero.
                File.Delete(partPath);
                throw new IOException($"Rango no válido al reanudar {displayName}");
            }

            response.EnsureSuccessStatusCode();

            var append = false;
            if (existing > 0)
            {
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    if (response.Content.Headers.ContentRange?.From != existing)
                    {
                        File.Delete(partPath);
                        throw new IOException($"El servidor devolvió un rango inesperado para {displayName}");
                    }

                    append = true;
                    _logger.LogInformation("Reanudando {File} desde {Bytes} bytes", displayName, existing);
                }
                else
                {
                    // El servidor no admite Range: se empieza desde cero.
                    hash.GetHashAndReset();
                    existing = 0;
                    report(0);
                }
            }

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var target = new FileStream(partPath, append ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            var written = existing;
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                timeout.CancelAfter(DownloadTimeout); // timeout de lectura, no total
                await target.WriteAsync(buffer.AsMemory(0, read), attemptCt).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                written += read;
                report(written);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (OperationCanceledException ex) when (!attemptCt.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout descargando {displayName}", ex);
        }
    }

    /// <summary>Reintentos con espera exponencial para errores transitorios (máx. <see cref="MaxAttempts"/>).</summary>
    private async Task<T> WithRetriesAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = RetryDelay * Math.Pow(2, attempt - 1);
                _logger.LogWarning("Error transitorio ({Message}); reintento {Attempt}/{Max} en {Delay}", ex.Message, attempt, MaxAttempts - 1, delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Errores de red, timeouts y HTTP 408/429/5xx. Un 404 o un hash incorrecto no se reintentan.</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException => true,
        IOException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } => true,
        HttpRequestException { StatusCode: { } code } => (int)code >= 500,
        _ => false,
    };

    // ══════════════════════════════════════════════════════════════════════
    // UTILIDADES
    // ══════════════════════════════════════════════════════════════════════

    // Temporales de versiones anteriores del launcher que quedaron de una ejecución interrumpida.
    [GeneratedRegex("^verify-([a-z0-9_]{8}|[0-9a-f]{32})$")]
    private static partial Regex StaleTempRegex();

    private void RemoveStaleTempDirs(string gameDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(gameDir, "verify-*"))
        {
            if (StaleTempRegex().IsMatch(Path.GetFileName(dir)))
            {
                _logger.LogInformation("Borrando temporal huérfano {Dir}", dir);
                TryDeleteDirectory(dir);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Se vuelve a intentar en la próxima verificación.
        }
    }

    private static void TryDeleteIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Acumula los bytes descargados de todas las descargas (seguro entre hilos).</summary>
    private sealed class DownloadTracker(long total, Action<long, long> report)
    {
        private long _current;

        public void Add(long delta)
        {
            if (delta == 0)
                return;
            var current = Interlocked.Add(ref _current, delta);
            report(Math.Clamp(current, 0, Math.Max(total, current)), total);
        }
    }
}
