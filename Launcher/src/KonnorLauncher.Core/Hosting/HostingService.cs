using System.Collections.Concurrent;
using KonnorLauncher.Core.Progress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Hosting;

/// <summary>
/// Servidores alojados en marcha: un <see cref="ServerRunner"/> (el juego) y un <see cref="ClientPublisher"/>
/// (la sincronización de los launchers) por servidor. Al cerrar el launcher se detienen todos con <c>stop</c>.
/// </summary>
public sealed class HostingService
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ServerRunner> _runners = new();
    private readonly ConcurrentDictionary<string, ClientPublisher> _publishers = new();

    public HostingService(HostedServerStore store, ServerInstaller installer, NetworkAccess network, ILoggerFactory? loggers = null)
    {
        Store = store;
        Installer = installer;
        Network = network;
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<HostingService>();
    }

    public HostedServerStore Store { get; }
    public ServerInstaller Installer { get; }
    public NetworkAccess Network { get; }

    public ServerRunner GetRunner(string id) =>
        _runners.GetOrAdd(id, key => new ServerRunner(key, _loggers.CreateLogger<ServerRunner>()));

    public ClientPublisher GetPublisher(string id) =>
        _publishers.GetOrAdd(id, _ => new ClientPublisher(_loggers.CreateLogger<ClientPublisher>()));

    public bool AnyRunning => _runners.Values.Any(r => r.IsAlive) || _publishers.Values.Any(p => p.IsRunning);

    /// <summary>
    /// Arranca el servidor (y su publicación, si está activada). Comprueba el EULA y que el puerto esté libre
    /// antes de lanzar Java, para dar un error claro en lugar de una consola llena de excepciones.
    /// </summary>
    public async Task StartAsync(HostedServer server, IProgress<LauncherProgress>? progress, CancellationToken ct = default)
    {
        var dir = Store.GetDirectory(server.Id);
        if (!server.Installed || server.LaunchArguments.Count == 0)
            throw new InvalidOperationException("El servidor no terminó de instalarse: reinstálalo desde Configuración.");
        if (!ServerInstaller.IsEulaAccepted(dir))
            throw new InvalidOperationException("Hay que aceptar el EULA de Minecraft para iniciar el servidor.");
        var port = ServerPropertiesFile.Load(Path.Combine(dir, ServerPropertiesFile.FileName)).Port;
        if (!NetworkAccess.IsPortFree(port))
            throw new InvalidOperationException($"El puerto {port} ya está en uso (¿otro servidor en marcha?). Cámbialo en Configuración.");

        var java = await Installer.ResolveJavaAsync(server, progress, ct).ConfigureAwait(false);
        // El paquete de recursos obligatorio se escribe en server.properties antes de que Minecraft lo lea.
        await PrepareSyncAsync(server, ct).ConfigureAwait(false);
        GetRunner(server.Id).Start(java, dir, server.LaunchArguments, server.MemoryMb);
        server.LastStarted = DateTimeOffset.Now;
        try
        {
            Store.Save(server);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "No se pudo guardar la fecha de inicio de {Id}", server.Id);
        }

        if (server.Publish.Enabled)
        {
            try
            {
                await StartPublishingAsync(server, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // El juego ya está en marcha: el error de la publicación se ve en su pestaña.
                _logger.LogWarning(ex, "No se pudo publicar el cliente de {Id}", server.Id);
            }
        }
    }

    public async Task StartPublishingAsync(HostedServer server, CancellationToken ct = default)
    {
        await PrepareSyncAsync(server, ct).ConfigureAwait(false);
        await GetPublisher(server.Id).StartAsync(server, Store.GetDirectory(server.Id), Store.GetClientDirectory(server.Id),
            Store.GetInternalDirectory(server.Id), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lo que depende de cómo llegan los jugadores: la dirección de <c>auto_join</c> y el paquete de recursos
    /// obligatorio de <c>server.properties</c> (descargado de la sincronización). Devuelve un aviso o null.
    /// </summary>
    public async Task<string?> PrepareSyncAsync(HostedServer server, CancellationToken ct = default)
    {
        var dir = Store.GetDirectory(server.Id);
        var host = await Network.GetPlayerHostAsync(server.Publish.PublicHost, ct).ConfigureAwait(false);
        var port = ServerPropertiesFile.Load(Path.Combine(dir, ServerPropertiesFile.FileName)).Port;
        server.Publish.JoinAddress = port == ServerPropertiesFile.DefaultPort ? host : $"{host}:{port}";
        var managed = server.Publish.ManagedResourcePackUrl;
        string? message;
        try
        {
            message = ServerResourcePack.Apply(server, dir, Store.GetClientDirectory(server.Id), $"http://{host}:{server.Publish.Port}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo aplicar el paquete de recursos obligatorio de {Id}", server.Id);
            return $"No se pudo configurar el paquete de recursos: {ex.Message}";
        }

        if (managed != server.Publish.ManagedResourcePackUrl)
        {
            try
            {
                Store.Save(server);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "No se pudo guardar {Id}", server.Id);
            }
        }

        return message;
    }

    public async Task StopAsync(string id)
    {
        if (_runners.TryGetValue(id, out var runner))
            await runner.StopAsync().ConfigureAwait(false);
        if (_publishers.TryGetValue(id, out var publisher))
            await publisher.StopAsync().ConfigureAwait(false);
    }

    /// <summary>Detiene todos los servidores a la vez (al cerrar el launcher).</summary>
    public Task StopAllAsync() =>
        Task.WhenAll(_runners.Keys.Concat(_publishers.Keys).Distinct().Select(StopAsync));

    /// <summary>Borra el servidor (detenido) con todos sus archivos.</summary>
    public async Task DeleteAsync(string id)
    {
        await StopAsync(id).ConfigureAwait(false);
        _runners.TryRemove(id, out _);
        _publishers.TryRemove(id, out _);
        Store.Delete(id);
    }
}
