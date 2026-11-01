using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KonnorLauncher.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Instances;

/// <summary>
/// Instancias en disco: <c>&lt;instancesDir&gt;/&lt;id&gt;/instance.json</c>. Crear, guardar, borrar y
/// migrar la configuración antigua de <c>launcher.properties</c> a una primera instancia.
/// </summary>
public sealed partial class InstanceStore
{
    public const string FileName = "instance.json";
    public const int MaxNameLength = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ILogger _logger;
    private readonly object _sync = new();

    public InstanceStore(string instancesDir, ILogger<InstanceStore>? logger = null)
    {
        InstancesDir = Path.GetFullPath(instancesDir);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string InstancesDir { get; }

    public string GetDirectory(string id) => Path.Combine(InstancesDir, id);

    // ── Lectura ──────────────────────────────────────────────────────────

    /// <summary>Todas las instancias, la jugada más recientemente primero. Las dañadas se ignoran.</summary>
    public List<GameInstance> LoadAll()
    {
        var result = new List<GameInstance>();
        if (!Directory.Exists(InstancesDir))
            return result;

        foreach (var dir in Directory.EnumerateDirectories(InstancesDir))
        {
            var file = Path.Combine(dir, FileName);
            if (!File.Exists(file))
                continue;
            try
            {
                var instance = JsonSerializer.Deserialize<GameInstance>(File.ReadAllText(file), JsonOptions);
                if (instance is null)
                    continue;
                instance.Id = Path.GetFileName(dir); // la carpeta manda, aunque el JSON diga otra cosa
                result.Add(instance);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Instancia ignorada: no se pudo leer {File}", file);
            }
        }

        return result
            .OrderByDescending(i => i.LastPlayed ?? DateTimeOffset.MinValue)
            .ThenByDescending(i => i.Created)
            .ToList();
    }

    // ── Escritura ────────────────────────────────────────────────────────

    /// <summary>Crea la instancia: le asigna un id único a partir del nombre y crea su carpeta.</summary>
    public GameInstance Create(GameInstance draft)
    {
        lock (_sync)
        {
            var instance = draft.Clone();
            instance.Name = ValidateName(instance.Name);
            var baseId = Slugify(instance.Name);
            var id = baseId;
            for (var n = 2; Directory.Exists(GetDirectory(id)); n++)
                id = $"{baseId}-{n}";
            instance.Id = id;
            instance.Created = DateTimeOffset.Now;
            Directory.CreateDirectory(Path.Combine(GetDirectory(id), "mods"));
            Write(instance);
            _logger.LogInformation("Instancia creada: {Name} ({Id}) {Summary}", instance.Name, id, instance.Summary);
            return instance;
        }
    }

    /// <summary>Guarda los cambios de una instancia existente.</summary>
    public void Save(GameInstance instance)
    {
        lock (_sync)
        {
            instance.Name = ValidateName(instance.Name);
            if (!Directory.Exists(GetDirectory(instance.Id)))
                throw new DirectoryNotFoundException($"No existe la instancia {instance.Id}");
            Write(instance);
        }
    }

    /// <summary>Borra la instancia y todo su contenido (mods, mundos, configuración...).</summary>
    public void Delete(string id)
    {
        lock (_sync)
        {
            var dir = GetDirectory(id);
            // Defensa: el id nunca debe salir de la carpeta de instancias.
            if (!Path.GetFullPath(dir).StartsWith(InstancesDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Id de instancia no válido: {id}");
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true); // en Windows no entra en uniones ni enlaces
            _logger.LogInformation("Instancia eliminada: {Id}", id);
        }
    }

    private void Write(GameInstance instance)
    {
        var file = Path.Combine(GetDirectory(instance.Id), FileName);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(instance, JsonOptions), Encoding.UTF8);
        File.Move(tmp, file, overwrite: true);
    }

    // ── Migración desde launcher.properties ──────────────────────────────

    /// <summary>
    /// Si todavía no hay instancias y <c>launcher.properties</c> tenía una versión o la verificación
    /// activada, crea la instancia "Konnor" con esa configuración. Devuelve la instancia creada.
    /// </summary>
    public GameInstance? MigrateLegacy(LauncherSettings settings)
    {
        if (LoadAll().Count > 0)
            return null;
        var version = settings.MinecraftVersion.Trim();
        if (version.Length == 0 && !settings.EnableVerify)
            return null;

        var instance = Create(new GameInstance
        {
            Name = "Konnor",
            MinecraftVersion = version,
            Loader = ModLoader.Vanilla, // la versión antigua ya era el id completo (p. ej. con Forge)
            SyncWithServer = settings.EnableVerify,
            ServerUrl = settings.ServerUrl.Trim(),
            ServerClientVersion = string.IsNullOrWhiteSpace(settings.CachedClientVersion) ? null : settings.CachedClientVersion,
        });
        _logger.LogInformation("Configuración antigua migrada a la instancia {Id}", instance.Id);
        return instance;
    }

    // ── Utilidades ───────────────────────────────────────────────────────

    public static string ValidateName(string? name)
    {
        var text = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
            throw new ArgumentException("El nombre no puede estar vacío");
        if (text.Length > MaxNameLength)
            throw new ArgumentException($"El nombre admite como mucho {MaxNameLength} caracteres");
        return text;
    }

    /// <summary>Nombre de carpeta a partir del nombre: minúsculas, sin acentos, letras, números y guiones.</summary>
    public static string Slugify(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var ascii = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && c < 128)
                ascii.Append(char.ToLowerInvariant(c));
        }

        var slug = NonSlugChars().Replace(ascii.ToString(), "-").Trim('-');
        if (slug.Length > 32)
            slug = slug[..32].Trim('-');
        return slug.Length == 0 ? "instancia" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();
}
