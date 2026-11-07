using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Installers;
using CmlLib.Core.Java;
using KonnorLauncher.Core.Launching;
using KonnorLauncher.Core.Loaders;
using KonnorLauncher.Core.Progress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Hosting;

/// <summary>
/// Descarga e instala el software de un servidor (lo que hacía ServerManager), con las fuentes oficiales:
/// <list type="bullet">
/// <item>Vanilla: el <c>server.jar</c> de Mojang (comprobado con su SHA-1).</item>
/// <item>Paper: API Fill v3 de PaperMC (comprobado con su SHA-256).</item>
/// <item>Fabric: el lanzador de servidor de <c>meta.fabricmc.net</c> y el <c>server.jar</c> de Mojang.</item>
/// <item>Quilt, Forge y NeoForge: su instalador oficial en modo servidor.</item>
/// </list>
/// El Java no se busca en el sistema: se usa el de Mojang adecuado para la versión (el mismo que usa el
/// juego, compartido en <c>meta/runtime</c>), que CmlLib descarga si falta.
/// </summary>
public sealed class ServerInstaller
{
    public const string FabricLauncherJar = "fabric-server-launch.jar";
    public const string QuiltLauncherJar = "quilt-server-launch.jar";
    public const string VanillaJar = "server.jar";
    public const string EulaFile = "eula.txt";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;
    private readonly string _metaDir;

    public ServerInstaller(IHttpClientFactory httpFactory, string metaDir, ILogger<ServerInstaller>? logger = null)
    {
        _httpFactory = httpFactory;
        _metaDir = metaDir;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Instala el servidor en <paramref name="dir"/> y rellena <see cref="HostedServer.LaunchArguments"/> y
    /// <see cref="HostedServer.JavaComponent"/>. Si <paramref name="acceptEula"/>, escribe <c>eula.txt</c>.
    /// </summary>
    public async Task InstallAsync(HostedServer server, string dir, bool acceptEula, IProgress<LauncherProgress>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        var report = new ThrottledProgress(progress);

        // 1. Java de Mojang para esta versión (y el JSON de la versión, que trae el server.jar de vanilla).
        report.Report(new LauncherProgress(LauncherPhase.Installing, "Preparando Java…", null));
        var (java, component, versionJson) = await EnsureJavaAsync(server.MinecraftVersion, report, ct).ConfigureAwait(false);
        server.JavaComponent = component;

        // 2. Software del servidor.
        var loaderHttp = _httpFactory.CreateClient(LoaderVersionService.HttpClientName);
        switch (server.Software)
        {
            case ServerSoftware.Vanilla:
                await DownloadVanillaAsync(versionJson, dir, report, ct).ConfigureAwait(false);
                server.LaunchArguments = ["-jar", VanillaJar];
                break;

            case ServerSoftware.Paper:
            {
                var builds = ServerSoftwareCatalog.ParsePaperBuilds(await loaderHttp.GetStringAsync(
                    $"{ServerSoftwareCatalog.PaperApi}/versions/{Uri.EscapeDataString(server.MinecraftVersion)}/builds", ct).ConfigureAwait(false));
                var build = builds.FirstOrDefault(b => b.Build == server.LoaderVersion)
                            ?? builds.FirstOrDefault(b => b.Stable) ?? builds.FirstOrDefault()
                            ?? throw new InvalidOperationException($"Paper no tiene builds para {server.MinecraftVersion}");
                if (build.Url is null)
                    throw new InvalidOperationException($"El build {build.Build} de Paper no tiene descarga");
                server.LoaderVersion = build.Build;
                await DownloadAsync(loaderHttp, build.Url, Path.Combine(dir, VanillaJar), build.Sha256, HashAlgorithmName.SHA256,
                    $"Descargando Paper {server.MinecraftVersion} (build {build.Build})", report, ct).ConfigureAwait(false);
                server.LaunchArguments = ["-jar", VanillaJar];
                break;
            }

            case ServerSoftware.Fabric:
            {
                var loader = RequireLoader(server);
                var installer = await LatestFabricInstallerAsync(loaderHttp, ct).ConfigureAwait(false);
                var url = $"{LoaderVersionService.FabricMeta}/versions/loader/{Uri.EscapeDataString(server.MinecraftVersion)}/{Uri.EscapeDataString(loader)}/{Uri.EscapeDataString(installer)}/server/jar";
                await DownloadAsync(loaderHttp, url, Path.Combine(dir, FabricLauncherJar), null, default,
                    $"Descargando Fabric {loader}", report, ct).ConfigureAwait(false);
                // El lanzador de Fabric descargaría el server.jar al arrancar: se deja ya descargado.
                await DownloadVanillaAsync(versionJson, dir, report, ct).ConfigureAwait(false);
                server.LaunchArguments = ["-jar", FabricLauncherJar];
                break;
            }

            case ServerSoftware.Quilt:
            {
                var loader = RequireLoader(server);
                var installerVersion = await LatestQuiltInstallerAsync(loaderHttp, ct).ConfigureAwait(false);
                var installer = Path.Combine(dir, "quilt-installer.jar");
                await DownloadAsync(loaderHttp,
                    $"https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/{installerVersion}/quilt-installer-{installerVersion}.jar",
                    installer, null, default, "Descargando el instalador de Quilt", report, ct).ConfigureAwait(false);
                await RunInstallerAsync(java, dir, ["-jar", installer, "install", "server", server.MinecraftVersion, loader, "--download-server", "--install-dir=."],
                    $"Instalando Quilt {loader}…", report, ct).ConfigureAwait(false);
                TryDelete(installer);
                server.LaunchArguments = ["-jar", QuiltLauncherJar];
                break;
            }

            case ServerSoftware.Forge:
            case ServerSoftware.NeoForge:
            {
                var loader = RequireLoader(server);
                var forge = server.Software == ServerSoftware.Forge;
                var name = forge ? "Forge" : "NeoForge";
                var url = forge
                    ? $"https://maven.minecraftforge.net/net/minecraftforge/forge/{server.MinecraftVersion}-{loader}/forge-{server.MinecraftVersion}-{loader}-installer.jar"
                    : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loader}/neoforge-{loader}-installer.jar";
                var installer = Path.Combine(dir, $"{name.ToLowerInvariant()}-installer.jar");
                await DownloadAsync(loaderHttp, url, installer, null, default, $"Descargando el instalador de {name}", report, ct).ConfigureAwait(false);
                await RunInstallerAsync(java, dir, ["-jar", installer, "--installServer"], $"Instalando {name} {loader}…", report, ct).ConfigureAwait(false);
                TryDelete(installer);
                server.LaunchArguments = FindForgeLaunch(dir, server.MinecraftVersion, loader, forge)
                    ?? throw new InvalidOperationException($"El instalador de {name} terminó, pero no se encontró cómo arrancar el servidor");
                break;
            }

            default:
                throw new NotSupportedException(server.Software.ToString());
        }

        // 3. EULA y configuración inicial.
        if (acceptEula)
            AcceptEula(dir);
        report.Report(new LauncherProgress(LauncherPhase.Installing, "Servidor instalado", 100));
        _logger.LogInformation("Servidor {Id} instalado: {Summary} {Loader} | {Args}", server.Id, server.Summary, server.LoaderVersion,
            string.Join(' ', server.LaunchArguments));
    }

    /// <summary>Escribe <c>eula=true</c> (el jugador aceptó el EULA de Minecraft al crear el servidor).</summary>
    public static void AcceptEula(string dir) =>
        File.WriteAllText(Path.Combine(dir, EulaFile),
            $"#Aceptado desde Konnor Launcher (https://aka.ms/MinecraftEULA) el {DateTimeOffset.Now:yyyy-MM-dd HH:mm}\neula=true\n");

    public static bool IsEulaAccepted(string dir)
    {
        var file = Path.Combine(dir, EulaFile);
        return File.Exists(file) && ServerPropertiesFile.Load(file).GetBool("eula", false);
    }

    // ── Java ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Java de Mojang para <paramref name="minecraftVersion"/>: CmlLib descarga solo el runtime (sin cliente,
    /// librerías ni assets). Devuelve <c>java.exe</c> (no <c>javaw</c>: el servidor escribe en la consola).
    /// </summary>
    public async Task<(string JavaPath, string? Component, string VersionJsonPath)> EnsureJavaAsync(
        string minecraftVersion, IProgress<LauncherProgress>? progress, CancellationToken ct)
    {
        var path = new MinecraftPath(_metaDir);
        var http = _httpFactory.CreateClient(GameLauncher.HttpClientName);
        var parameters = MinecraftLauncherParameters.CreateDefault(path, http);
        parameters.FileExtractors = new FileExtractorCollection { new JavaFileExtractor(http, parameters.JavaPathResolver!) };
        var launcher = new MinecraftLauncher(parameters);

        var version = await launcher.GetVersionAsync(minecraftVersion, ct).ConfigureAwait(false);
        var bytes = new SyncProgress<ByteProgress>(b =>
        {
            if (b.TotalBytes > 0)
                progress?.Report(new LauncherProgress(LauncherPhase.Installing, "Descargando Java…", Math.Clamp(b.ToRatio() * 100, 0, 100)));
        });
        await launcher.InstallAsync(version, null, bytes, ct).ConfigureAwait(false);

        var javaw = launcher.GetJavaPath(version) ?? throw new InvalidOperationException("No se pudo preparar Java");
        var java = Path.Combine(Path.GetDirectoryName(javaw)!, OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (!File.Exists(java))
            java = javaw;
        var versionJson = Path.Combine(path.Versions, minecraftVersion, minecraftVersion + ".json");
        return (java, version.JavaVersion?.Component, versionJson);
    }

    /// <summary>Ruta de Java de un servidor ya instalado (sin red si el runtime ya está en disco).</summary>
    public async Task<string> ResolveJavaAsync(HostedServer server, IProgress<LauncherProgress>? progress, CancellationToken ct)
    {
        if (server.JavaComponent is { Length: > 0 } component)
        {
            var launcher = new MinecraftLauncher(new MinecraftPath(_metaDir));
            var javaw = launcher.JavaPathResolver.GetJavaBinaryPath(new JavaVersion(component), launcher.RulesContext);
            var java = Path.Combine(Path.GetDirectoryName(javaw)!, "java.exe");
            if (File.Exists(java))
                return java;
        }

        return (await EnsureJavaAsync(server.MinecraftVersion, progress, ct).ConfigureAwait(false)).JavaPath;
    }

    // ── Descargas ────────────────────────────────────────────────────────

    private async Task DownloadVanillaAsync(string versionJsonPath, string dir, IProgress<LauncherProgress> progress, CancellationToken ct)
    {
        var (url, sha1) = ReadVanillaServerDownload(await File.ReadAllTextAsync(versionJsonPath, ct).ConfigureAwait(false));
        if (url is null)
            throw new InvalidOperationException("Mojang no publica un servidor para esta versión");
        var target = Path.Combine(dir, VanillaJar);
        if (sha1 is not null && File.Exists(target) && await HashFileAsync(target, HashAlgorithmName.SHA1, ct).ConfigureAwait(false) == sha1)
            return;
        await DownloadAsync(_httpFactory.CreateClient(GameLauncher.HttpClientName), url, target, sha1, HashAlgorithmName.SHA1,
            "Descargando el servidor de Minecraft", progress, ct).ConfigureAwait(false);
    }

    /// <summary>URL y SHA-1 de <c>downloads.server</c> en el JSON de una versión de Mojang.</summary>
    public static (string? Url, string? Sha1) ReadVanillaServerDownload(string versionJson)
    {
        using var doc = JsonDocument.Parse(versionJson);
        if (doc.RootElement.TryGetProperty("downloads", out var downloads)
            && downloads.TryGetProperty("server", out var server)
            && server.TryGetProperty("url", out var url))
        {
            var sha = server.TryGetProperty("sha1", out var s) ? s.GetString()?.ToLowerInvariant() : null;
            return (url.GetString(), sha);
        }

        return (null, null);
    }

    /// <summary>Descarga a un temporal, comprueba el hash (si se conoce) y reemplaza el destino.</summary>
    private static async Task DownloadAsync(HttpClient http, string url, string target, string? expectedHash, HashAlgorithmName algorithm,
        string message, IProgress<LauncherProgress> progress, CancellationToken ct)
    {
        progress.Report(new LauncherProgress(LauncherPhase.Downloading, message, 0));
        var tmp = target + ".part";
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException($"No se encontró la descarga ({url})");
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0)
                    progress.Report(new LauncherProgress(LauncherPhase.Downloading, message, Math.Min(100, done * 100.0 / total.Value)));
            }
        }

        if (expectedHash is not null)
        {
            var actual = await HashFileAsync(tmp, algorithm, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tmp);
                throw new InvalidOperationException("El archivo descargado está dañado (el hash no coincide). Vuelve a intentarlo.");
            }
        }

        File.Move(tmp, target, overwrite: true);
    }

    private static async Task<string> HashFileAsync(string path, HashAlgorithmName algorithm, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<string> LatestFabricInstallerAsync(HttpClient http, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"{LoaderVersionService.FabricMeta}/versions/installer", ct).ConfigureAwait(false));
        var entries = doc.RootElement.EnumerateArray().ToList();
        var pick = entries.FirstOrDefault(e => e.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True);
        if (pick.ValueKind == JsonValueKind.Undefined)
            pick = entries.FirstOrDefault();
        return pick.ValueKind != JsonValueKind.Undefined && pick.TryGetProperty("version", out var v) && v.GetString() is { } version
            ? version
            : throw new InvalidOperationException("No se pudo obtener el instalador de Fabric");
    }

    private static async Task<string> LatestQuiltInstallerAsync(HttpClient http, CancellationToken ct)
    {
        var xml = await http.GetStringAsync("https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/maven-metadata.xml", ct).ConfigureAwait(false);
        return ReadMavenRelease(xml) ?? throw new InvalidOperationException("No se pudo obtener el instalador de Quilt");
    }

    /// <summary><c>&lt;release&gt;</c> (o el último <c>&lt;version&gt;</c>) de un <c>maven-metadata.xml</c>.</summary>
    public static string? ReadMavenRelease(string xml)
    {
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var release = doc.Descendants("release").FirstOrDefault()?.Value;
        return !string.IsNullOrWhiteSpace(release) ? release.Trim() : doc.Descendants("version").LastOrDefault()?.Value.Trim();
    }

    // ── Instaladores (Quilt, Forge, NeoForge) ────────────────────────────

    private async Task RunInstallerAsync(string java, string dir, IReadOnlyList<string> args, string message,
        IProgress<LauncherProgress> progress, CancellationToken ct)
    {
        progress.Report(new LauncherProgress(LauncherPhase.Installing, message, null));
        var start = new ProcessStartInfo(java)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        var tail = new Queue<string>();
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 20)
                    tail.Dequeue();
            }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string last;
            lock (tail)
                last = string.Join(Environment.NewLine, tail);
            _logger.LogWarning("El instalador terminó con código {Code}:\n{Output}", process.ExitCode, last);
            throw new InvalidOperationException($"El instalador terminó con un error (código {process.ExitCode}). {tail.LastOrDefault()}");
        }
    }

    /// <summary>
    /// Cómo arrancar Forge/NeoForge tras <c>--installServer</c>: desde 1.17, con el archivo de argumentos
    /// (<c>@libraries/.../win_args.txt</c>); antes, con el jar de Forge que deja el instalador.
    /// </summary>
    public static List<string>? FindForgeLaunch(string dir, string minecraftVersion, string loaderVersion, bool forge)
    {
        var argsFile = forge
            ? Path.Combine("libraries", "net", "minecraftforge", "forge", $"{minecraftVersion}-{loaderVersion}", "win_args.txt")
            : Path.Combine("libraries", "net", "neoforged", "neoforge", loaderVersion, "win_args.txt");
        if (File.Exists(Path.Combine(dir, argsFile)))
        {
            var result = new List<string>();
            if (File.Exists(Path.Combine(dir, "user_jvm_args.txt")))
                result.Add("@user_jvm_args.txt");
            result.Add("@" + argsFile.Replace('\\', '/'));
            return result;
        }

        var jar = Directory.EnumerateFiles(dir, "*.jar", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(n => n is not null && n.StartsWith(forge ? "forge-" : "neoforge-", StringComparison.OrdinalIgnoreCase)
                        && !n.Contains("installer", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n!.Contains("universal", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        return jar is null ? null : ["-jar", jar];
    }

    private static string RequireLoader(HostedServer server) =>
        string.IsNullOrWhiteSpace(server.LoaderVersion)
            ? throw new InvalidOperationException($"Falta la versión de {HostedServer.SoftwareName(server.Software)}")
            : server.LoaderVersion!;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
