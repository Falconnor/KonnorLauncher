using System.Text;
using System.Text.Json;
using KonnorLauncher.Core.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Hosting;

/// <summary>
/// Servidores alojados en disco: <c>&lt;hostingDir&gt;/&lt;id&gt;/konnor-server.json</c> junto a los archivos
/// del servidor. Las rutas que publica el cliente (<c>cliente/</c>) y los datos internos
/// (<c>.konnor/</c>: manifest y caché de hashes) también viven ahí.
/// </summary>
public sealed class HostedServerStore
{
    public const string FileName = "konnor-server.json";
    /// <summary>Archivos que se publican a los jugadores además de los mods del servidor.</summary>
    public const string ClientFolder = "cliente";
    /// <summary>Manifest generado y caché de hashes.</summary>
    public const string InternalFolder = ".konnor";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ILogger _logger;
    private readonly object _sync = new();

    public HostedServerStore(string hostingDir, ILogger<HostedServerStore>? logger = null)
    {
        HostingDir = Path.GetFullPath(hostingDir);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string HostingDir { get; }

    public string GetDirectory(string id) => Path.Combine(HostingDir, id);
    public string GetClientDirectory(string id) => Path.Combine(GetDirectory(id), ClientFolder);
    public string GetInternalDirectory(string id) => Path.Combine(GetDirectory(id), InternalFolder);

    /// <summary>Todos los servidores, el usado más recientemente primero. Los dañados se ignoran.</summary>
    public List<HostedServer> LoadAll()
    {
        var result = new List<HostedServer>();
        if (!Directory.Exists(HostingDir))
            return result;

        foreach (var dir in Directory.EnumerateDirectories(HostingDir))
        {
            var file = Path.Combine(dir, FileName);
            if (!File.Exists(file))
                continue;
            try
            {
                var server = JsonSerializer.Deserialize<HostedServer>(File.ReadAllText(file), JsonOptions);
                if (server is null)
                    continue;
                server.Id = Path.GetFileName(dir);
                result.Add(server);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Servidor ignorado: no se pudo leer {File}", file);
            }
        }

        return result
            .OrderByDescending(s => s.LastStarted ?? DateTimeOffset.MinValue)
            .ThenByDescending(s => s.Created)
            .ToList();
    }

    /// <summary>Crea la carpeta del servidor con un id único a partir del nombre.</summary>
    public HostedServer Create(HostedServer draft)
    {
        lock (_sync)
        {
            var server = draft.Clone();
            server.Name = InstanceStore.ValidateName(server.Name);
            var baseId = InstanceStore.Slugify(server.Name);
            if (baseId == "instancia")
                baseId = "servidor";
            var id = baseId;
            for (var n = 2; Directory.Exists(GetDirectory(id)); n++)
                id = $"{baseId}-{n}";
            server.Id = id;
            server.Created = DateTimeOffset.Now;
            Directory.CreateDirectory(GetDirectory(id));
            Write(server);
            _logger.LogInformation("Servidor creado: {Name} ({Id}) {Summary}", server.Name, id, server.Summary);
            return server;
        }
    }

    public void Save(HostedServer server)
    {
        lock (_sync)
        {
            server.Name = InstanceStore.ValidateName(server.Name);
            if (!Directory.Exists(GetDirectory(server.Id)))
                throw new DirectoryNotFoundException($"No existe el servidor {server.Id}");
            Write(server);
        }
    }

    /// <summary>Borra el servidor con su mundo y todos sus archivos.</summary>
    public void Delete(string id)
    {
        lock (_sync)
        {
            var dir = Path.GetFullPath(GetDirectory(id));
            if (string.IsNullOrWhiteSpace(id) || !dir.StartsWith(HostingDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Id de servidor no válido: {id}");
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Servidor eliminado: {Id}", id);
        }
    }

    /// <summary>Puerto de Minecraft libre para un servidor nuevo: 25565 o el siguiente que no use otro.</summary>
    public int NextFreeGamePort()
    {
        var used = LoadAll()
            .Select(s => ServerPropertiesFile.Load(Path.Combine(GetDirectory(s.Id), ServerPropertiesFile.FileName)).Port)
            .ToHashSet();
        var port = ServerPropertiesFile.DefaultPort;
        while (used.Contains(port))
            port++;
        return port;
    }

    /// <summary>Puerto de publicación libre: 8000 o el siguiente que no use otro servidor.</summary>
    public int NextFreePublishPort(string? exceptId = null)
    {
        var used = LoadAll().Where(s => s.Id != exceptId).Select(s => s.Publish.Port).ToHashSet();
        var port = ClientPublishSettings.DefaultPort;
        while (used.Contains(port))
            port++;
        return port;
    }

    private void Write(HostedServer server)
    {
        var file = Path.Combine(GetDirectory(server.Id), FileName);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(server, JsonOptions), Encoding.UTF8);
        File.Move(tmp, file, overwrite: true);
    }
}
