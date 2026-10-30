using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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

    /// <summary><c>delete_unauthorized</c>: limpieza de archivos no listados.</summary>
    public bool DeleteUnauthorized { get; init; }

    /// <summary>Modo simulación (<c>--dry-run</c>): no descarga, no reemplaza, no borra.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Si la carpeta del juego es la <c>.minecraft</c> oficial, la limpieza no se ejecuta, para no borrar
    /// los mods ni las versiones del launcher de Mojang. En false, se limpia igualmente.
    /// </summary>
    public bool SkipCleanupInOfficialDir { get; init; } = true;

    /// <summary>Descargas simultáneas de archivos sueltos (1 = una detrás de otra).</summary>
    public int MaxParallelDownloads { get; init; } = 4;
}

/// <summary>
/// Sincroniza el cliente con el servidor. Mantiene exactamente el mismo protocolo que la versión
/// en Python (endpoints, formato del manifest y textos de estado), así el servidor no necesita cambios.
/// </summary>
public sealed partial class ClientVerifier
{
    /// <summary>Las únicas carpetas que la limpieza puede tocar (nunca saves, config, options.txt...).</summary>
    public static readonly IReadOnlyList<string> ProtectedFolders = ["mods", "versions", "libraries", "assets"];

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

        string? tempDir = null;
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

            // 4-5. Qué hay que reparar (existencia + tamaño; el hash solo valida lo descargado).
            var repairBlocks = FindBlocksToRepair(gameDir, manifest.AssetBlocks);
            var repairFiles = FindFilesToRepair(gameDir, manifest.Files);
            _logger.LogInformation("Manifest: {Files} archivos, {Blocks} bloques. A reparar: {RFiles} archivos, {RBlocks} bloques",
                manifest.Files.Count, manifest.AssetBlocks.Count, repairFiles.Count, repairBlocks.Count);

            // 6. Simulación: informa sin tocar nada.
            if (options.DryRun)
            {
                Status($"Simulación: {repairBlocks.Count} bloques y {repairFiles.Count} archivos requieren reparación");
                Status(VerificationResult.SuccessStatus);
                return new VerificationResult(true, VerificationResult.SuccessStatus, repairBlocks.Count, repairFiles.Count);
            }

            // 7. Carpeta temporal dentro del juego (mismo volumen → reemplazo atómico).
            tempDir = Path.Combine(gameDir, "verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var totalBytes = repairBlocks.Sum(b => b.Size) + repairFiles.Sum(f => f.Size);
            var tracker = new DownloadTracker(totalBytes, (current, total) =>
                Status(FormatDownloadStatus(current, total), total > 0 ? Math.Min(100, current * 100.0 / total) : 0, LauncherPhase.Downloading));

            // 8. Fase 1: bloques (descarga secuencial; la extracción de uno solapa con la descarga del siguiente).
            await RepairBlocksAsync(baseUrl, gameDir, tempDir, repairBlocks, tracker, ct).ConfigureAwait(false);

            // 9. Fase 2: archivos sueltos con paralelismo limitado.
            await RepairFilesAsync(baseUrl, gameDir, tempDir, repairFiles, tracker, options.MaxParallelDownloads, ct).ConfigureAwait(false);

            // 10. Limpieza (solo si todo lo anterior tuvo éxito).
            var removed = 0;
            if (options.DeleteUnauthorized)
            {
                if (options.SkipCleanupInOfficialDir && LauncherPaths.IsOfficialMinecraftDir(gameDir))
                {
                    _logger.LogWarning("Limpieza omitida: la carpeta del juego es la .minecraft oficial ({GameDir}) (decisión D1)", gameDir);
                }
                else
                {
                    var allowed = manifest.Files.Concat(manifest.AssetBlocks.SelectMany(b => b.Files))
                        .Select(f => ManifestPath.ComparisonKey(f.RelativePath))
                        .ToHashSet(StringComparer.Ordinal);
                    removed = RemoveUnauthorizedFiles(gameDir, allowed);
                    _logger.LogInformation("Limpieza: {Removed} archivos no autorizados eliminados", removed);
                }
            }

            // 11. Fin.
            Status(VerificationResult.SuccessStatus);
            return new VerificationResult(true, VerificationResult.SuccessStatus, repairBlocks.Count, repairFiles.Count, removed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Verificación cancelada");
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
            // El temporal se borra siempre, incluso con error o cancelación.
            if (tempDir is not null)
                TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>Texto visible durante las descargas (siempre con punto decimal).</summary>
    public static string FormatDownloadStatus(long current, long total) =>
        string.Create(CultureInfo.InvariantCulture, $"descargando archivos: {current / BytesPerMb:F2} MB / {total / BytesPerMb:F2} MB");

    // ══════════════════════════════════════════════════════════════════════
    // QUÉ HAY QUE REPARAR
    // ══════════════════════════════════════════════════════════════════════

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
    // FASE 1: BLOQUES DE ASSETS
    // ══════════════════════════════════════════════════════════════════════

    private async Task RepairBlocksAsync(string baseUrl, string gameDir, string tempDir, List<AssetBlock> blocks, DownloadTracker tracker, CancellationToken ct)
    {
        // Las extracciones se encadenan con un solo "trabajador": cada una espera a la anterior.
        var extraction = Task.CompletedTask;
        try
        {
            foreach (var block in blocks)
            {
                // Si una extracción anterior ya falló, no se sigue descargando.
                if (extraction.IsFaulted)
                    break;

                var zipPath = Path.Combine(tempDir, block.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                var url = $"{baseUrl}/client/packages/{ManifestPath.EncodeUrlPath(block.Name)}";
                await DownloadFileAsync(url, zipPath, block.Sha256, Path.GetFileName(block.Name), tracker, ct).ConfigureAwait(false);

                var previous = extraction;
                extraction = Task.Run(async () =>
                {
                    await previous.ConfigureAwait(false);
                    ExtractBlock(zipPath, gameDir, block, ct);
                }, ct);
            }
        }
        catch
        {
            // Falló una descarga: se espera a la extracción en curso antes de borrar el temporal
            // y se propaga el error de descarga (el de extracción, si lo hay, queda en segundo plano).
            try
            {
                await extraction.ConfigureAwait(false);
            }
            catch
            {
                // Se informa el error de la descarga.
            }

            throw;
        }

        // Espera a que terminen todas las extracciones; si alguna falló, el error sale aquí.
        await extraction.ConfigureAwait(false);
    }

    /// <summary>Extrae SOLO las entradas listadas en el manifest y después borra el ZIP.</summary>
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

                // Se extrae a un .part y luego se reemplaza: así nunca queda un archivo final a medias.
                var partial = destination + ".part";
                entry.ExtractToFile(partial, overwrite: true);
                var length = new FileInfo(partial).Length;
                if (length != file.Size)
                {
                    // Se avisa en el log, pero no bloquea el juego.
                    _logger.LogWarning("Tamaño extraído distinto al manifest: {Path} ({Actual} != {Expected})", file.UrlPath, length, file.Size);
                }

                File.Move(partial, destination, overwrite: true);
            }
        }

        File.Delete(zipPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    // FASE 2: ARCHIVOS SUELTOS
    // ══════════════════════════════════════════════════════════════════════

    private async Task RepairFilesAsync(string baseUrl, string gameDir, string tempDir, List<ManifestFile> files, DownloadTracker tracker, int maxParallel, CancellationToken ct)
    {
        if (files.Count == 0)
            return;

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallel), CancellationToken = ct };
        await Parallel.ForEachAsync(files, parallelOptions, async (file, token) =>
        {
            // Descarga a la carpeta temporal calculando el hash en streaming...
            var downloaded = Path.Combine(tempDir, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(downloaded)!);
            var url = $"{baseUrl}/client/{ManifestPath.EncodeUrlPath(file.UrlPath)}";
            await DownloadFileAsync(url, downloaded, file.Sha256, Path.GetFileName(file.RelativePath), tracker, token).ConfigureAwait(false);

            // ...y se mueve a su destino de forma atómica.
            var destination = ManifestPath.ToLocalPath(gameDir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(downloaded, destination, overwrite: true);
        }).ConfigureAwait(false);
    }

    // ══════════════════════════════════════════════════════════════════════
    // LIMPIEZA
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Borra, solo dentro de <see cref="ProtectedFolders"/>, todo archivo cuya ruta relativa no esté
    /// en <paramref name="allowedKeys"/> (claves de <see cref="ManifestPath.ComparisonKey"/>).
    /// No sigue enlaces simbólicos ni uniones, así que nunca sale de esas carpetas.
    /// </summary>
    public static int RemoveUnauthorizedFiles(string gameDir, IReadOnlySet<string> allowedKeys)
    {
        var removed = 0;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var folder in ProtectedFolders)
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
    // RED: MANIFEST Y DESCARGAS CON HASH
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
    /// Descarga en streaming calculando el SHA-256 mientras escribe.
    /// Solo reintenta errores transitorios de red; un hash incorrecto falla sin reintentar.
    /// </summary>
    private async Task DownloadFileAsync(string url, string destination, string expectedHash, string displayName, DownloadTracker tracker, CancellationToken ct)
    {
        string actualHash;
        try
        {
            actualHash = await WithRetriesAsync(async attemptCt =>
            {
                long reported = 0;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attemptCt);
                    timeout.CancelAfter(DownloadTimeout);
                    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        timeout.CancelAfter(DownloadTimeout); // timeout de lectura, no total
                        await target.WriteAsync(buffer.AsMemory(0, read), attemptCt).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        reported += read;
                        tracker.Add(read);
                    }

                    return Convert.ToHexString(hash.GetHashAndReset());
                }
                catch (OperationCanceledException ex) when (!attemptCt.IsCancellationRequested)
                {
                    tracker.Add(-reported);
                    throw new TimeoutException($"Timeout descargando {displayName}", ex);
                }
                catch
                {
                    // Un reintento vuelve a empezar, así que se descuenta lo que reportó este intento.
                    tracker.Add(-reported);
                    throw;
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
        {
            throw new VerificationException($"No se pudo descargar {displayName}", ex);
        }

        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(destination); // El archivo inválido no se deja ni siquiera en el temporal.
            throw new VerificationException($"Hash inválido en la descarga: {displayName}");
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

    // Temporales que quedaron de una ejecución interrumpida (tanto de la versión en Python como de esta).
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
            // Se vuelve a intentar en la próxima verificación (RemoveStaleTempDirs).
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
            var current = Interlocked.Add(ref _current, delta);
            report(Math.Clamp(current, 0, Math.Max(total, current)), total);
        }
    }
}
