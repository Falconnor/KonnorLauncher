using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Hosting;

/// <summary>Actividad desde que se empezó a publicar (lo que mostraba el panel de LauncherBackEnd).</summary>
public sealed record PublishActivity(int Verifications, int FilesServed, int DistinctClients, int Errors);

/// <summary>
/// Publica el cliente de un servidor para los launchers de los jugadores: el equivalente de
/// LauncherBackEnd (FastAPI + uvicorn) con Kestrel, el servidor web de .NET, dentro del propio launcher.
/// <list type="bullet">
/// <item><c>GET /</c>: estado (para comprobarlo desde un navegador).</item>
/// <item><c>GET /client_manifest.json</c>: el manifest, sin caché.</item>
/// <item><c>GET /client/{ruta}</c>: solo los archivos que lista el manifest (nada más del disco es accesible),
/// con soporte de rangos.</item>
/// </list>
/// Vigila la carpeta del cliente y los mods del servidor y regenera el manifest cuando dejan de cambiar.
/// </summary>
public sealed class ClientPublisher : IAsyncDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly HashSet<string> _clients = [];
    private WebApplication? _app;
    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _debounce;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private HostedServer? _server;
    private string _serverDir = string.Empty;
    private string _clientDir = string.Empty;
    private string _internalDir = string.Empty;
    private volatile ManifestBuildResult? _current;
    private int _verifications, _filesServed, _errors;

    public ClientPublisher(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    public bool IsRunning => _app is not null;
    public int Port { get; private set; }
    public ManifestBuildResult? Current => _current;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastBuilt { get; private set; }
    public bool IsBuilding { get; private set; }

    public PublishActivity Activity
    {
        get { lock (_sync) return new PublishActivity(_verifications, _filesServed, _clients.Count, _errors); }
    }

    /// <summary>Cambió el estado (arranque, parada, manifest regenerado, actividad).</summary>
    public event EventHandler? Changed;

    public string ManifestPath => Path.Combine(_internalDir, "client_manifest.json");

    /// <summary>Genera el manifest y empieza a servir en todas las interfaces, en <c>server.Publish.Port</c>.</summary>
    public async Task StartAsync(HostedServer server, string serverDir, string clientDir, string internalDir, CancellationToken ct = default)
    {
        if (IsRunning)
            await StopAsync().ConfigureAwait(false);

        _server = server.Clone();
        _serverDir = serverDir;
        _clientDir = clientDir;
        _internalDir = internalDir;
        Directory.CreateDirectory(clientDir);
        lock (_sync)
        {
            _clients.Clear();
            _verifications = _filesServed = _errors = 0;
        }

        await RegenerateAsync(ct).ConfigureAwait(false);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = internalDir, Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o =>
        {
            o.AddServerHeader = false;
            o.ListenAnyIP(server.Publish.Port);
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip))
                lock (_sync)
                    _clients.Add(ip.ToString());
            await next(context).ConfigureAwait(false);
            if (context.Response.StatusCode >= 400)
                Interlocked.Increment(ref _errors);
        });
        app.MapGet("/", () => Results.Json(new
        {
            status = "online",
            server = "Konnor Launcher",
            profile = new { id = _server?.Id, name = _server?.Name },
            manifest = _current is not null,
            client_version = _server?.Publish.ClientVersion,
            files = _current?.FileCount ?? 0,
        }));
        app.MapGet("/client_manifest.json", (HttpContext context) =>
        {
            var manifest = _current;
            if (manifest is null)
            {
                context.Response.Headers.RetryAfter = "10";
                return Results.Json(new { detail = "Generando el manifest" }, statusCode: 503);
            }

            Interlocked.Increment(ref _verifications);
            context.Response.Headers.CacheControl = "no-store";
            Notify();
            return Results.Bytes(manifest.ManifestBytes, "application/json");
        });
        app.MapGet("/client/{**path}", (string path) =>
        {
            var manifest = _current;
            if (manifest is null || !manifest.PhysicalFiles.TryGetValue(path, out var file) || !File.Exists(file))
                return Results.NotFound();
            Interlocked.Increment(ref _filesServed);
            return Results.File(file, "application/octet-stream", enableRangeProcessing: true);
        });

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            LastError = ex is IOException
                ? $"El puerto {server.Publish.Port} está en uso: elige otro en la configuración."
                : ex.Message;
            Notify();
            throw new InvalidOperationException(LastError, ex);
        }

        _app = app;
        Port = server.Publish.Port;
        LastError = null;
        Watch();
        _logger.LogInformation("Publicando el cliente de {Id} en el puerto {Port} ({Files} archivos)", server.Id, Port, _current?.FileCount);
        Notify();
    }

    public async Task StopAsync()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
        _debounce?.Cancel();
        var app = _app;
        _app = null;
        if (app is not null)
        {
            try
            {
                await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            await app.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Publicación del cliente detenida");
        }

        Notify();
    }

    /// <summary>Actualiza la configuración (versión, política...) sin reiniciar y regenera el manifest.</summary>
    public async Task UpdateSettingsAsync(HostedServer server)
    {
        _server = server.Clone();
        await RegenerateAsync().ConfigureAwait(false);
    }

    /// <summary>Vuelve a generar el manifest (solo se recalculan los hashes de lo que cambió).</summary>
    public async Task RegenerateAsync(CancellationToken ct = default)
    {
        var server = _server;
        if (server is null)
            return;
        await _buildLock.WaitAsync(ct).ConfigureAwait(false);
        IsBuilding = true;
        Notify();
        try
        {
            var result = await Task.Run(() => ClientManifestBuilder.Build(server, _serverDir, _clientDir, ManifestPath,
                new HashCache(Path.Combine(_internalDir, "hashes.json"))), ct).ConfigureAwait(false);
            _current = result;
            LastBuilt = DateTimeOffset.Now;
            LastError = result.InvalidPaths.Count > 0
                ? $"{result.InvalidPaths.Count} archivos no se publican porque su nombre no es válido para el launcher: {string.Join(", ", result.InvalidPaths.Take(3))}"
                : null;
            _logger.LogInformation("Manifest de {Id} {State}: {Files} archivos en {Ms} ms", server.Id,
                result.Changed ? "actualizado" : "sin cambios", result.FileCount, (int)result.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"No se pudo generar el manifest: {ex.Message}";
            _logger.LogWarning(ex, "No se pudo generar el manifest de {Id}", server.Id);
        }
        finally
        {
            IsBuilding = false;
            _buildLock.Release();
            Notify();
        }
    }

    private void Watch()
    {
        foreach (var dir in new[] { _clientDir, Path.Combine(_serverDir, "mods") })
        {
            if (!Directory.Exists(dir))
                continue;
            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => Schedule();
            watcher.Created += (_, _) => Schedule();
            watcher.Deleted += (_, _) => Schedule();
            watcher.Renamed += (_, _) => Schedule();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    /// <summary>Regenera cuando los archivos llevan unos segundos sin cambiar (no a mitad de una copia).</summary>
    private void Schedule()
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            _debounce?.Cancel();
            cts = _debounce = new CancellationTokenSource();
        }

        _ = Task.Delay(Debounce, cts.Token).ContinueWith(async t =>
        {
            if (!t.IsCanceled && IsRunning)
                await RegenerateAsync().ConfigureAwait(false);
        }, TaskScheduler.Default);
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
