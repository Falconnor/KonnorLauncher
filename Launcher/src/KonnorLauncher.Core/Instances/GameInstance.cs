using System.Text.Json.Serialization;
using CmlLib.Core;

namespace KonnorLauncher.Core.Instances;

/// <summary>Cargador de mods de una instancia.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModLoader>))]
public enum ModLoader
{
    Vanilla,
    Fabric,
    Forge,
    NeoForge,
    Quilt,
}

/// <summary>Cómo se eligió la versión del cargador: la estable, la última o una concreta.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LoaderChannel>))]
public enum LoaderChannel
{
    Stable,
    Latest,
    Specific,
}

/// <summary>
/// Una instancia del juego: su versión de Minecraft, su cargador de mods y su carpeta propia
/// (mods, config, mundos...). Se guarda como <c>instance.json</c> dentro de su carpeta.
/// </summary>
public sealed class GameInstance
{
    /// <summary>Identificador estable: es también el nombre de su carpeta.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Versión de Minecraft (por ejemplo <c>1.21.1</c>). En una instancia sincronizada también
    /// puede ser una versión que publica el servidor (por ejemplo <c>1.20.1-forge-47.3.0</c>).
    /// </summary>
    public string MinecraftVersion { get; set; } = string.Empty;

    public ModLoader Loader { get; set; } = ModLoader.Vanilla;

    public LoaderChannel LoaderChannel { get; set; } = LoaderChannel.Stable;

    /// <summary>Versión concreta del cargador, resuelta al guardar la instancia (null en vanilla).</summary>
    public string? LoaderVersion { get; set; }

    /// <summary>Versión que se lanzó la última vez (la que crea el cargador). Permite jugar sin conexión.</summary>
    public string? InstalledVersionId { get; set; }

    // ── Sincronización con LauncherBackEnd ───────────────────────────────

    /// <summary>
    /// Si está activa, al jugar se verifican los archivos con el servidor. La instancia guarda
    /// entonces sus propios <c>versions</c>, <c>libraries</c> y <c>assets</c>: la verificación borra lo
    /// que no está en el manifest y nunca debe tocar los archivos compartidos de otras instancias.
    /// </summary>
    public bool SyncWithServer { get; set; }

    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Borrar de mods, versions, libraries y assets lo que no esté en el manifest.</summary>
    public bool DeleteUnauthorized { get; set; } = true;

    /// <summary>Última <c>client_version</c> que anunció el servidor.</summary>
    public string? ServerClientVersion { get; set; }

    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastPlayed { get; set; }

    /// <summary>Texto corto para la interfaz: "1.21.1 · Fabric 0.16.10".</summary>
    [JsonIgnore]
    public string Summary => Loader == ModLoader.Vanilla
        ? MinecraftVersion
        : $"{MinecraftVersion} · {LoaderDisplayName(Loader)}{(LoaderVersion is null ? string.Empty : " " + LoaderVersion)}";

    public static string LoaderDisplayName(ModLoader loader) => loader switch
    {
        ModLoader.NeoForge => "NeoForge",
        _ => loader.ToString(),
    };

    public GameInstance Clone() => (GameInstance)MemberwiseClone();
}

/// <summary>Carpetas de una instancia y cómo se pasan a CmlLib.</summary>
public sealed record InstanceLayout(string GameDir, string VersionsDir, string LibrariesDir, string AssetsDir, string RuntimeDir)
{
    /// <summary>
    /// Instancia normal: versions, libraries y assets compartidos en <paramref name="metaDir"/>.
    /// Instancia sincronizada: todo dentro de su carpeta. El Java de Mojang siempre es compartido.
    /// </summary>
    public static InstanceLayout For(GameInstance instance, string instancesDir, string metaDir)
    {
        var gameDir = Path.Combine(instancesDir, instance.Id);
        var root = instance.SyncWithServer ? gameDir : metaDir;
        return new InstanceLayout(
            gameDir,
            Path.Combine(root, "versions"),
            Path.Combine(root, "libraries"),
            Path.Combine(root, "assets"),
            Path.Combine(metaDir, "runtime"));
    }

    public MinecraftPath ToMinecraftPath() => new(GameDir)
    {
        Versions = VersionsDir,
        Library = LibrariesDir,
        Assets = AssetsDir,
        Runtime = RuntimeDir,
    };

    public void EnsureCreated()
    {
        foreach (var dir in new[] { GameDir, VersionsDir, LibrariesDir, AssetsDir, RuntimeDir, Path.Combine(GameDir, "mods") })
            Directory.CreateDirectory(dir);
    }
}
