using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installers;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Progress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Loaders;

/// <summary>
/// Instala el cargador de mods de una instancia y devuelve el id de versión que hay que lanzar.
/// <list type="bullet">
/// <item>Vanilla: la propia versión de Minecraft.</item>
/// <item>Fabric y Quilt: su servicio oficial genera el JSON de la versión (con <c>inheritsFrom</c> a
/// vanilla); se guarda en <c>versions/&lt;id&gt;/</c> y CmlLib descarga el resto.</item>
/// <item>Forge y NeoForge: CmlLib.Core.Installer.Forge descarga el instalador oficial y ejecuta sus
/// procesadores con el Java de Mojang.</item>
/// </list>
/// </summary>
public sealed class LoaderInstaller
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;

    public LoaderInstaller(IHttpClientFactory httpFactory, ILogger<LoaderInstaller>? logger = null)
    {
        _httpFactory = httpFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task<string> InstallAsync(GameInstance instance, MinecraftLauncher launcher, MinecraftPath path,
        IProgress<LauncherProgress>? progress, CancellationToken ct = default)
    {
        if (instance.Loader == ModLoader.Vanilla)
            return instance.MinecraftVersion;
        if (string.IsNullOrWhiteSpace(instance.LoaderVersion))
            throw new InvalidOperationException($"La instancia no tiene versión de {GameInstance.LoaderDisplayName(instance.Loader)}");

        var loaderName = GameInstance.LoaderDisplayName(instance.Loader);
        var status = $"Instalando {loaderName} {instance.LoaderVersion}...";
        progress?.Report(new LauncherProgress(LauncherPhase.Installing, status, null));
        var http = _httpFactory.CreateClient(LoaderVersionService.HttpClientName);
        var mc = instance.MinecraftVersion;
        var loader = instance.LoaderVersion!;

        var id = instance.Loader switch
        {
            ModLoader.Fabric => await InstallProfileAsync(http, path,
                $"{LoaderVersionService.FabricMeta}/versions/loader/{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loader)}/profile/json", ct),
            ModLoader.Quilt => await InstallProfileAsync(http, path,
                $"{LoaderVersionService.QuiltMeta}/versions/loader/{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loader)}/profile/json", ct),
            ModLoader.Forge => await new ForgeInstaller(launcher, http).Install(mc, loader, Options(status, progress, ct)),
            ModLoader.NeoForge => await new NeoForgeInstaller(launcher, http).Install(mc, loader, Options(status, progress, ct)),
            _ => throw new NotSupportedException(instance.Loader.ToString()),
        };

        _logger.LogInformation("{Loader} {LoaderVersion} para {Minecraft} instalado como {Id}", loaderName, loader, mc, id);
        return id;
    }

    /// <summary>Opciones del instalador de Forge/NeoForge: no repite la instalación y avisa del progreso.</summary>
    private static ForgeInstallOptions Options(string status, IProgress<LauncherProgress>? progress, CancellationToken ct) => new()
    {
        SkipIfAlreadyInstalled = true,
        CancellationToken = ct,
        ByteProgress = new SyncProgress<ByteProgress>(b =>
        {
            if (b.TotalBytes > 0)
                progress?.Report(new LauncherProgress(LauncherPhase.Installing, status, Math.Clamp(b.ToRatio() * 100, 0, 100)));
        }),
    };

    /// <summary>
    /// Descarga el JSON de versión de Fabric o Quilt y lo guarda en <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>.
    /// Si ya existe no se vuelve a pedir.
    /// </summary>
    private static async Task<string> InstallProfileAsync(HttpClient http, MinecraftPath path, string url, CancellationToken ct)
    {
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        string id;
        using (var doc = JsonDocument.Parse(json))
        {
            id = doc.RootElement.GetProperty("id").GetString()
                 ?? throw new InvalidDataException("El perfil del cargador no tiene id");
        }

        var target = path.GetVersionJsonPath(id);
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, target, overwrite: true);
        }

        return id;
    }

    /// <summary>IProgress que no captura el contexto de la UI (CmlLib emite muchos eventos).</summary>
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
