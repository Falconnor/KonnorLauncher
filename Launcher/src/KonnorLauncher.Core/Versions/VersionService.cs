using CmlLib.Core.VersionLoader;
using CmlLib.Core.VersionMetadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Versions;

/// <summary>Elemento de la lista de versiones.</summary>
public sealed record VersionEntry(string Id, bool IsInstalled, string Type);

/// <summary>
/// Listado y selección de versiones. Primero aparecen las instaladas (en orden ordinal) y después
/// las releases oficiales de Mojang que todavía no están instaladas (sin snapshots ni betas).
/// </summary>
public sealed class VersionService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;
    private volatile IReadOnlyList<string>? _remoteReleases;

    public VersionService(IHttpClientFactory httpFactory, ILogger<VersionService>? logger = null)
    {
        _httpFactory = httpFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Nombre del HttpClient con el que se consulta el manifest de Mojang.</summary>
    public const string HttpClientName = "mojang";

    /// <summary>
    /// null = la lista remota aún no se cargó ("Cargando versiones oficiales...").
    /// Lista vacía = se intentó y no hay conexión.
    /// </summary>
    public IReadOnlyList<string>? RemoteReleases => _remoteReleases;

    /// <summary>
    /// Versiones instaladas: subcarpetas de <c>versions/</c> que contienen <c>&lt;id&gt;.json</c>
    /// o cualquier <c>.json</c>, en orden ordinal.
    /// </summary>
    public static List<string> GetInstalledVersions(string versionsDir)
    {
        var result = new List<string>();
        if (!Directory.Exists(versionsDir))
            return result;

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(versionsDir).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var name in dirs.Order(StringComparer.Ordinal))
        {
            var dir = Path.Combine(versionsDir, name);
            try
            {
                if (File.Exists(Path.Combine(dir, name + ".json")) || Directory.EnumerateFiles(dir, "*.json").Any())
                    result.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Si la carpeta no se puede leer, se ignora.
            }
        }

        return result;
    }

    /// <summary>
    /// Descarga la lista de Mojang (solo <c>release</c>) con el cargador de CmlLib.
    /// Sin conexión deja la lista vacía en vez de lanzar una excepción.
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchRemoteReleasesAsync(CancellationToken ct = default)
    {
        try
        {
            var loader = new MojangJsonVersionLoader(_httpFactory.CreateClient(HttpClientName));
            var collection = await loader.GetVersionMetadatasAsync(ct).ConfigureAwait(false);
            var releases = collection
                .Where(v => v.GetVersionType() == MVersionType.Release)
                .OrderByDescending(v => v.ReleaseTime) // mismo orden que el manifest (más reciente primero)
                .Select(v => v.Name)
                .ToList();
            _remoteReleases = releases;
            _logger.LogInformation("Lista de Mojang cargada: {Count} releases", releases.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo obtener la lista de versiones de Mojang");
            _remoteReleases = [];
        }

        return _remoteReleases;
    }

    /// <summary>Combina instaladas y remotas sin duplicados (función pura, testeable).</summary>
    public static List<VersionEntry> Combine(IReadOnlyList<string> installed, IReadOnlyList<string>? remote)
    {
        var result = installed.Select(id => new VersionEntry(id, true, "installed")).ToList();
        var seen = installed.ToHashSet(StringComparer.Ordinal);
        foreach (var id in remote ?? [])
        {
            if (seen.Add(id))
                result.Add(new VersionEntry(id, false, "release"));
        }

        return result;
    }
}
