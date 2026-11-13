using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using KonnorLauncher.Core.Modrinth;
using KonnorLauncher.Core.Paths;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.Services;

/// <summary>
/// Icono de un archivo de contenido que no trae el suyo dentro (casi todos los shaders, algunos paquetes y mods): se
/// busca el archivo en Modrinth por su SHA-512 y se usa el icono de su proyecto. Las peticiones de varios archivos se
/// juntan en una sola consulta, y lo encontrado (también «no está en Modrinth») se guarda en disco por ruta, tamaño y
/// fecha: al volver a abrir el launcher no se calculan hashes ni se pregunta otra vez.
/// </summary>
public sealed class ContentIconResolver
{
    /// <summary>Lo usa <c>ModFileItem</c>, que no se crea con inyección de dependencias (se fija al arrancar).</summary>
    public static ContentIconResolver? Current { get; set; }

    private const long MaxHashBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(250);

    private readonly ModrinthClient _client;
    private readonly IconLoader _icons;
    private readonly ILogger<ContentIconResolver> _logger;
    private readonly string _cacheFile = Path.Combine(LauncherPaths.CacheDir, "content-icons.json");

    /// <summary>Archivo («ruta|tamaño|fecha») → URL del icono ("" = no está en Modrinth o no tiene icono).</summary>
    private readonly ConcurrentDictionary<string, string> _known;

    private readonly object _batchLock = new();
    private List<(string Hash, TaskCompletionSource<string?> Result)> _pending = [];
    private bool _flushScheduled;

    public ContentIconResolver(ModrinthClient client, IconLoader icons, ILogger<ContentIconResolver> logger)
    {
        _client = client;
        _icons = icons;
        _logger = logger;
        _known = new ConcurrentDictionary<string, string>(LoadCache(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Icono del proyecto de Modrinth al que pertenece el archivo; null si no está, no tiene o no hay conexión.</summary>
    public async Task<ImageSource?> ResolveAsync(string path)
    {
        string key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > MaxHashBytes)
                return null;
            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (!_known.TryGetValue(key, out var url))
        {
            string hash;
            try
            {
                hash = await Task.Run(() => Sha512(path)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            url = await LookupAsync(hash).ConfigureAwait(false);
            if (url is null)
                return null; // sin conexión: se vuelve a intentar la próxima vez
            _known[key] = url;
            SaveCache();
        }

        return url.Length == 0 ? null : await _icons.LoadAsync(url, 96).ConfigureAwait(false);
    }

    /// <summary>URL del icono para ese hash ("" = no está en Modrinth); null si la consulta falló. Se agrupa con las demás.</summary>
    private Task<string?> LookupAsync(string hash)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_batchLock)
        {
            _pending.Add((hash.ToLowerInvariant(), result));
            if (!_flushScheduled)
            {
                _flushScheduled = true;
                _ = FlushLaterAsync();
            }
        }

        return result.Task;
    }

    private async Task FlushLaterAsync()
    {
        await Task.Delay(BatchDelay).ConfigureAwait(false);
        List<(string Hash, TaskCompletionSource<string?> Result)> batch;
        lock (_batchLock)
        {
            batch = _pending;
            _pending = [];
            _flushScheduled = false;
        }

        try
        {
            var versions = await _client.FindVersionsBySha512Async(batch.Select(b => b.Hash)).ConfigureAwait(false);
            var projects = await _client.GetProjectsAsync(versions.Values.Select(v => v.ProjectId).Distinct()).ConfigureAwait(false);
            var icons = projects.ToDictionary(p => p.Id, p => p.IconUrl ?? string.Empty);
            foreach (var (hash, result) in batch)
            {
                result.TrySetResult(versions.TryGetValue(hash, out var version) && icons.TryGetValue(version.ProjectId, out var icon)
                    ? icon
                    : string.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("No se pudieron buscar iconos en Modrinth: {Message}", ex.Message);
            foreach (var (_, result) in batch)
                result.TrySetResult(null);
        }
    }

    private static string Sha512(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA512.HashData(stream));
    }

    private Dictionary<string, string> LoadCache()
    {
        try
        {
            if (File.Exists(_cacheFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_cacheFile)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug("Caché de iconos de contenido ilegible: se empieza de cero ({Message})", ex.Message);
        }

        return [];
    }

    private readonly object _saveLock = new();

    private void SaveCache()
    {
        try
        {
            lock (_saveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                var tmp = _cacheFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_known.ToDictionary(p => p.Key, p => p.Value)));
                File.Move(tmp, _cacheFile, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("No se pudo guardar la caché de iconos de contenido: {Message}", ex.Message);
        }
    }
}
