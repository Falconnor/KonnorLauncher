using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
public sealed partial class GameInstance
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

    /// <summary>Memoria para esta instancia en MB. null = la de Opciones (<c>java.memory</c>).</summary>
    public int? MemoryMb { get; set; }

    /// <summary>Versión que se lanzó la última vez (la que crea el cargador). Permite jugar sin conexión.</summary>
    public string? InstalledVersionId { get; set; }

    // ── Sincronización con LauncherBackEnd ───────────────────────────────

    /// <summary>
    /// Si está activa, al jugar se verifican los archivos con el servidor. La instancia guarda
    /// entonces sus propios <c>versions</c>, <c>libraries</c> y <c>assets</c>: la verificación borra lo
    /// que no está en el manifest y nunca debe tocar los archivos compartidos de otras instancias.
    /// Si el manifest publica la versión (<c>minecraft</c>), esas carpetas las llena el launcher y la
    /// limpieza solo afecta a <c>mods</c> (y a los packs si el servidor los protege).
    /// </summary>
    public bool SyncWithServer { get; set; }

    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// La instancia se creó o sincronizó con éxito con su servidor: queda vinculada y el jugador no
    /// puede desactivar la sincronización ni cambiar de servidor hasta que el servidor la libere.
    /// </summary>
    public bool SyncLocked { get; set; }

    /// <summary>El servidor liberó sus instancias (<c>released</c>). Irreversible, también aquí.</summary>
    public bool ServerReleased { get; set; }

    /// <summary>
    /// Última política <c>always_online</c> del servidor. Se recuerda porque, justo cuando el servidor
    /// no responde, no hay manifest que leer. true = sin respuesta no se juega.
    /// </summary>
    public bool ServerAlwaysOnline { get; set; } = true;

    /// <summary>
    /// Última política <c>lock_server_url</c> del servidor (reversible): true = el jugador no puede
    /// cambiar la dirección del servidor de esta instancia.
    /// </summary>
    public bool ServerLocksUrl { get; set; } = true;

    /// <summary>
    /// "Instalar paquetes de recursos": el jugador quiere los resourcepacks y shaderpacks del servidor.
    /// Si el servidor no los protege, solo se instalan los que no tenga ya con el mismo nombre.
    /// </summary>
    public bool InstallPacks { get; set; }

    /// <summary>
    /// Última política <c>protect_packs</c> del servidor (reversible): true = los packs se instalan
    /// siempre y se protegen como el resto; el jugador no puede desmarcar la casilla.
    /// </summary>
    public bool ServerProtectsPacks { get; set; }

    /// <summary>
    /// <c>auto_join</c> del servidor: dirección del servidor de Minecraft al que el juego entra directamente
    /// al pulsar JUGAR (null = el servidor no lo pide).
    /// </summary>
    public string? ServerAutoJoin { get; set; }

    /// <summary>
    /// <c>recommended_memory_mb</c> del servidor: la RAM que usa la instancia mientras el jugador no elija la
    /// suya (<see cref="MemoryMb"/>). null = el servidor no la propone.
    /// </summary>
    public int? ServerMemoryMb { get; set; }

    /// <summary>
    /// RAM de la partida: la elegida para la instancia; si no, la que recomienda su servidor (ajustada a la
    /// del equipo); si no, <paramref name="defaultMb"/> (la de Opciones).
    /// </summary>
    public int ResolveMemoryMb(int defaultMb) =>
        MemoryMb ?? (SyncWithServer && ServerMemoryMb is { } server ? Configuration.SystemMemory.Clamp(server) : defaultMb);

    /// <summary>Vinculada y no liberada: la sincronización no se puede desactivar.</summary>
    [JsonIgnore]
    public bool IsSyncLocked => SyncWithServer && SyncLocked && !ServerReleased;

    /// <summary>Ya se sincronizó con su servidor y este no deja cambiar la dirección.</summary>
    [JsonIgnore]
    public bool IsUrlLocked => SyncWithServer && ServerLocksUrl && (SyncLocked || ServerReleased);

    /// <summary>El servidor obliga a instalar sus packs: la casilla queda marcada y bloqueada.</summary>
    [JsonIgnore]
    public bool IsPacksLocked => SyncWithServer && ServerProtectsPacks;

    /// <summary>
    /// <c>client_version</c> instalada: la del servidor en la última sincronización que terminó con
    /// éxito (con los archivos ya descargados).
    /// </summary>
    public string? ServerClientVersion { get; set; }

    /// <summary>
    /// <c>client_version</c> que anuncia ahora el servidor (se consulta al abrir el launcher, sin
    /// descargar nada). Si es posterior a la instalada, hay una actualización pendiente.
    /// </summary>
    public string? AvailableClientVersion { get; set; }

    /// <summary>El servidor publicó una versión del cliente posterior a la instalada.</summary>
    [JsonIgnore]
    public bool HasUpdate => SyncWithServer && ClientVersions.IsNewer(AvailableClientVersion, ServerClientVersion);

    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastPlayed { get; set; }

    /// <summary>
    /// Versión de Minecraft y cargador tal como se muestran. En una instancia sincronizada la versión
    /// es el id que publica el servidor (p. ej. <c>fabric-loader-0.19.3-1.21.1</c>): se interpreta.
    /// </summary>
    [JsonIgnore]
    public (string Minecraft, ModLoader Loader, string? LoaderVersion) Display =>
        Loader == ModLoader.Vanilla ? ParseVersionId(MinecraftVersion) : (MinecraftVersion, Loader, LoaderVersion);

    /// <summary>Texto corto para la interfaz: "1.21.1 · Fabric 0.16.10".</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var (minecraft, loader, version) = Display;
            return loader == ModLoader.Vanilla
                ? minecraft
                : $"{minecraft} · {LoaderDisplayName(loader)}{(version is null ? string.Empty : " " + version)}";
        }
    }

    /// <summary>
    /// Interpreta los ids de versión de los cargadores: <c>fabric-loader-0.19.3-1.21.1</c>,
    /// <c>quilt-loader-0.20.0-beta.9-1.20.1</c>, <c>1.20.1-forge-47.3.0</c>,
    /// <c>1.12.2-forge1.12.2-14.23.5.2859</c> y <c>neoforge-21.1.77</c>. Si no es ninguno, es vanilla.
    /// </summary>
    public static (string Minecraft, ModLoader Loader, string? LoaderVersion) ParseVersionId(string id)
    {
        if (FabricId().Match(id) is { Success: true } fabric)
            return (fabric.Groups["mc"].Value, ModLoader.Fabric, fabric.Groups["lv"].Value);
        if (QuiltId().Match(id) is { Success: true } quilt)
            return (quilt.Groups["mc"].Value, ModLoader.Quilt, quilt.Groups["lv"].Value);
        if (ForgeId().Match(id) is { Success: true } forge)
            return (forge.Groups["mc"].Value, ModLoader.Forge, forge.Groups["lv"].Value);
        if (NeoForgeId().Match(id) is { Success: true } neo)
        {
            // NeoForge 21.1.x es para Minecraft 1.21.1; 20.4.x para 1.20.4.
            var major = int.Parse(neo.Groups["major"].Value);
            var minor = int.Parse(neo.Groups["minor"].Value);
            var minecraft = major is >= 20 and < 26 ? (minor == 0 ? $"1.{major}" : $"1.{major}.{minor}") : $"{major}.{minor}";
            return (minecraft, ModLoader.NeoForge, neo.Groups["lv"].Value);
        }

        return (id, ModLoader.Vanilla, null);
    }

    private const string McPattern = @"\d+\.\d+(?:\.\d+)?";

    [GeneratedRegex("^fabric-loader-(?<lv>.+)-(?<mc>" + McPattern + ")$")]
    private static partial Regex FabricId();

    [GeneratedRegex("^quilt-loader-(?<lv>.+)-(?<mc>" + McPattern + ")$")]
    private static partial Regex QuiltId();

    [GeneratedRegex("^(?<mc>" + McPattern + ")-forge-?(?:" + McPattern + "-)?(?<lv>.+)$")]
    private static partial Regex ForgeId();

    [GeneratedRegex(@"^neoforge-(?<lv>(?<major>\d+)\.(?<minor>\d+)\..+)$")]
    private static partial Regex NeoForgeId();

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
