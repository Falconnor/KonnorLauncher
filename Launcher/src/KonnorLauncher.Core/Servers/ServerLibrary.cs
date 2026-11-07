using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Servers;

/// <summary>Servidor guardado por el jugador en «Mis servidores».</summary>
public sealed record SavedServer(string Name, string Address, DateTimeOffset AddedAt);

/// <summary>
/// Lo que el launcher recuerda de los servidores (<c>servers.json</c> en <see cref="Directory"/>):
/// «Mis servidores» y la instancia creada para cada servidor del directorio de Modrinth (así «Jugar» no
/// crea otra cada vez). Si el archivo falta o está dañado, se empieza de cero.
/// </summary>
public sealed class ServerLibrary
{
    public const string FileName = "servers.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly ILogger _logger;
    private readonly object _sync = new();

    public ServerLibrary(string directory, ILogger<ServerLibrary>? logger = null)
    {
        Directory = Path.GetFullPath(directory);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string Directory { get; }

    private string FilePath => Path.Combine(Directory, FileName);

    /// <summary>Mis servidores, en el orden en que se añadieron.</summary>
    public IReadOnlyList<SavedServer> Saved()
    {
        lock (_sync)
            return Read().Saved;
    }

    /// <summary>
    /// Añade un servidor. La dirección debe ser válida (<see cref="ServerAddress"/>); si ya estaba, solo se
    /// actualiza su nombre. Sin nombre se usa la dirección.
    /// </summary>
    public SavedServer Add(string address, string? name)
    {
        if (!ServerAddress.TryParse(address, out var parsed))
            throw new ArgumentException("La dirección no es válida. Escríbela como «servidor.com» o «servidor.com:25565».", nameof(address));
        var normalized = parsed.ToString();
        var entry = new SavedServer(string.IsNullOrWhiteSpace(name) ? normalized : name.Trim(), normalized, DateTimeOffset.Now);
        lock (_sync)
        {
            var data = Read();
            var index = data.Saved.FindIndex(s => s.Address == normalized);
            if (index >= 0)
                data.Saved[index] = entry with { AddedAt = data.Saved[index].AddedAt };
            else
                data.Saved.Add(entry);
            Write(data);
            return index >= 0 ? data.Saved[index] : entry;
        }
    }

    public bool Remove(string address)
    {
        lock (_sync)
        {
            var data = Read();
            if (data.Saved.RemoveAll(s => s.Address == address) == 0)
                return false;
            Write(data);
            return true;
        }
    }

    /// <summary>Instancia que se creó para el servidor <paramref name="projectId"/> de Modrinth, si se recuerda.</summary>
    public string? LinkedInstance(string projectId)
    {
        lock (_sync)
            return Read().Instances.GetValueOrDefault(projectId);
    }

    public void LinkInstance(string projectId, string instanceId)
    {
        lock (_sync)
        {
            var data = Read();
            data.Instances[projectId] = instanceId;
            Write(data);
        }
    }

    private sealed class Data
    {
        public List<SavedServer> Saved { get; set; } = [];
        public Dictionary<string, string> Instances { get; set; } = [];
    }

    private Data Read()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new Data();
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Json) ?? new Data();
            data.Saved = data.Saved.Where(s => s is not null && ServerAddress.TryParse(s.Address, out _)).ToList();
            data.Instances ??= [];
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "No se pudo leer {File}", FilePath);
            return new Data();
        }
    }

    private void Write(Data data)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}
