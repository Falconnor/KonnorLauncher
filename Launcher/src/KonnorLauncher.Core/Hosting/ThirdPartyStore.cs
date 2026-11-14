using System.Text.Json;
using System.Text.Json.Serialization;

namespace KonnorLauncher.Core.Hosting;

/// <summary>Proveedores de hosting de terceros que se pueden añadir.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThirdPartyProvider>))]
public enum ThirdPartyProvider
{
    /// <summary>Con su API oficial: control completo desde el launcher.</summary>
    Exaroton,
    Aternos,
    Hostinger,
    Modrinth,

    /// <summary>Cualquier otro hosting: su panel web.</summary>
    Other,
}

/// <summary>
/// Un hosting de terceros guardado. exaroton: una cuenta (su token, cifrado por la aplicación) cuyos servidores se
/// listan al abrir. El resto: el panel web del servidor, que se abre dentro del launcher, y su dirección de juego para
/// ver si está en línea y cuántos jugadores tiene.
/// </summary>
public sealed class ThirdPartyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public ThirdPartyProvider Provider { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Panel web (no exaroton).</summary>
    public string? PanelUrl { get; set; }

    /// <summary>Dirección para entrar al servidor (opcional; para el estado y los jugadores).</summary>
    public string? JoinAddress { get; set; }

    /// <summary>exaroton: el token, ya cifrado (la aplicación lo cifra con la cuenta de Windows; nunca va en claro).</summary>
    public string? ProtectedToken { get; set; }

    public DateTimeOffset Added { get; set; } = DateTimeOffset.Now;

    public static string ProviderName(ThirdPartyProvider provider) => provider switch
    {
        ThirdPartyProvider.Exaroton => "exaroton",
        ThirdPartyProvider.Aternos => "Aternos",
        ThirdPartyProvider.Hostinger => "Hostinger",
        ThirdPartyProvider.Modrinth => "Modrinth Servers",
        _ => "Panel web",
    };

    /// <summary>Panel de cada proveedor (el jugador inicia sesión ahí la primera vez).</summary>
    public static string? DefaultPanel(ThirdPartyProvider provider) => provider switch
    {
        ThirdPartyProvider.Aternos => "https://aternos.org/servers/",
        ThirdPartyProvider.Hostinger => "https://hpanel.hostinger.com/",
        ThirdPartyProvider.Modrinth => "https://modrinth.com/servers/manage",
        _ => null,
    };

    /// <summary>Solo paneles web https (o http) válidos.</summary>
    public static bool IsValidPanel(string? url, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) && parsed is { Scheme: "https" or "http" } && (uri = parsed) is not null;
    }
}

/// <summary>Hostings de terceros guardados (<c>terceros.json</c> en la carpeta de datos).</summary>
public sealed class ThirdPartyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _file;
    private readonly object _sync = new();

    public ThirdPartyStore(string dataDir) => _file = Path.Combine(dataDir, "terceros.json");

    public List<ThirdPartyEntry> LoadAll()
    {
        lock (_sync)
        {
            try
            {
                return File.Exists(_file) ? JsonSerializer.Deserialize<List<ThirdPartyEntry>>(File.ReadAllText(_file), JsonOptions) ?? [] : [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public void Add(ThirdPartyEntry entry)
    {
        lock (_sync)
        {
            var all = LoadAll();
            all.Add(entry);
            Write(all);
        }
    }

    public void Remove(string id)
    {
        lock (_sync)
        {
            var all = LoadAll();
            all.RemoveAll(e => e.Id == id);
            Write(all);
        }
    }

    private void Write(List<ThirdPartyEntry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOptions));
        File.Move(tmp, _file, overwrite: true);
    }
}
