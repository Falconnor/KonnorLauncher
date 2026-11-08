using System.Text.Json.Serialization;
using KonnorLauncher.Core.Instances;

namespace KonnorLauncher.Core.Hosting;

/// <summary>Software del servidor: Vanilla, Paper (plugins) o un cargador de mods.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerSoftware>))]
public enum ServerSoftware
{
    Vanilla,
    Paper,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
}

/// <summary>
/// Un servidor de Minecraft alojado en este equipo (sección Hosting). Se guarda en
/// <c>hosting/&lt;id&gt;/konnor-server.json</c>; los archivos del servidor (mundo, mods, plugins,
/// server.properties...) están en la misma carpeta.
/// </summary>
public sealed class HostedServer
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ServerSoftware Software { get; set; } = ServerSoftware.Vanilla;
    public string MinecraftVersion { get; set; } = string.Empty;

    /// <summary>Versión del cargador (Fabric, Quilt, Forge, NeoForge) o build de Paper.</summary>
    public string? LoaderVersion { get; set; }

    /// <summary>Memoria máxima para el servidor (MB).</summary>
    public int MemoryMb { get; set; } = 4096;

    /// <summary>
    /// Cómo se arranca, relativo a la carpeta del servidor: <c>-jar server.jar</c> o, en Forge y NeoForge
    /// modernos, <c>@libraries/.../win_args.txt</c>. Lo rellena el instalador.
    /// </summary>
    public List<string> LaunchArguments { get; set; } = [];

    /// <summary>Componente de Java de Mojang que usa (p. ej. <c>java-runtime-delta</c>).</summary>
    public string? JavaComponent { get; set; }

    /// <summary>Instalación terminada (si se interrumpe, el servidor se puede reinstalar).</summary>
    public bool Installed { get; set; }

    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastStarted { get; set; }

    /// <summary>Arrancar el servidor al abrir el launcher.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Publicación del cliente para los launchers de los jugadores (sincronización).</summary>
    public ClientPublishSettings Publish { get; set; } = new();

    [JsonIgnore]
    public bool IsModded => Software is ServerSoftware.Fabric or ServerSoftware.Quilt or ServerSoftware.Forge or ServerSoftware.NeoForge;

    /// <summary>Carpeta de contenido propia del software: <c>plugins</c> en Paper, <c>mods</c> con un cargador.</summary>
    [JsonIgnore]
    public string? ContentFolder => Software switch
    {
        ServerSoftware.Paper => "plugins",
        ServerSoftware.Vanilla => null,
        _ => "mods",
    };

    /// <summary>Cargador que deben usar los jugadores (Paper y Vanilla: el juego sin cargador).</summary>
    [JsonIgnore]
    public ModLoader ClientLoader => Software switch
    {
        ServerSoftware.Fabric => ModLoader.Fabric,
        ServerSoftware.Quilt => ModLoader.Quilt,
        ServerSoftware.Forge => ModLoader.Forge,
        ServerSoftware.NeoForge => ModLoader.NeoForge,
        _ => ModLoader.Vanilla,
    };

    /// <summary>"Fabric 1.21.1", "Paper 1.21.4"...</summary>
    [JsonIgnore]
    public string Summary => $"{SoftwareName(Software)} {MinecraftVersion}";

    public static string SoftwareName(ServerSoftware software) => software switch
    {
        ServerSoftware.NeoForge => "NeoForge",
        _ => software.ToString(),
    };

    public HostedServer Clone()
    {
        var copy = (HostedServer)MemberwiseClone();
        copy.LaunchArguments = [.. LaunchArguments];
        copy.Publish = Publish.Clone();
        return copy;
    }
}

/// <summary>
/// Publicación del cliente: lo que hacía LauncherBackEnd. Un pequeño servidor HTTP sirve
/// <c>client_manifest.json</c> y los archivos del cliente para que los launchers con
/// «Sincronizar con el servidor» tengan exactamente los mismos mods.
/// </summary>
public sealed class ClientPublishSettings
{
    public const int DefaultPort = 8000;

    /// <summary>Publicar al arrancar el servidor de Minecraft (y detenerlo con él).</summary>
    public bool Enabled { get; set; }

    public int Port { get; set; } = DefaultPort;

    /// <summary>Versión del cliente que se anuncia; los launchers marcan «Actualización disponible» si sube.</summary>
    public string ClientVersion { get; set; } = "1.0.0";

    /// <summary>Publicar también los mods de la carpeta <c>mods</c> del servidor.</summary>
    public bool IncludeServerMods { get; set; } = true;

    // Política que se anuncia en el manifest (mismos campos que LauncherBackEnd).
    public bool DeleteUnauthorized { get; set; } = true;
    public bool AlwaysOnline { get; set; }
    public bool LockServerUrl { get; set; } = true;
    public bool ProtectPacks { get; set; }

    /// <summary>
    /// RAM que se propone a los jugadores (<c>recommended_memory_mb</c>), en MB; null = no se propone. La
    /// usa la instancia mientras el jugador no elija otra.
    /// </summary>
    public int? RecommendedMemoryMb { get; set; }

    /// <summary>
    /// Seguridad avanzada (<c>verify_hashes</c>): el launcher de cada jugador comprueba el SHA-256 de todos los
    /// archivos al sincronizar. Desactivada, solo el nombre y el tamaño (mucho más rápido).
    /// </summary>
    public bool VerifyHashes { get; set; }

    /// <summary>Al jugar, el juego de cada jugador entra directamente a este servidor (<c>auto_join</c>).</summary>
    public bool AutoJoin { get; set; } = true;

    /// <summary>
    /// Dirección con la que llegan los jugadores (un dominio o la IP pública fija). Vacía = la IP pública
    /// detectada. Se usa para <c>auto_join</c> y para la descarga del paquete de recursos obligatorio.
    /// </summary>
    public string PublicHost { get; set; } = string.Empty;

    /// <summary>
    /// <c>resource-pack</c> que puso el launcher en <c>server.properties</c> (para quitarlo solo si es el suyo).
    /// </summary>
    public string? ManagedResourcePackUrl { get; set; }

    /// <summary>Dirección del servidor de Minecraft que se publica (la calcula el servicio al publicar).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? JoinAddress { get; set; }

    public ClientPublishSettings Clone() => (ClientPublishSettings)MemberwiseClone();
}
