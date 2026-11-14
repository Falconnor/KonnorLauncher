using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using KonnorLauncher.Core.Progress;

namespace KonnorLauncher.Core.Hosting;

/// <summary>Lo que se reconoció de un servidor existente: software, versiones y cómo arrancarlo.</summary>
public sealed record ImportedServer(ServerSoftware Software, string MinecraftVersion, string? LoaderVersion, IReadOnlyList<string> LaunchArguments)
{
    public string Summary => LoaderVersion is { Length: > 0 } loader
        ? $"{HostedServer.SoftwareName(Software)} {MinecraftVersion} ({loader})"
        : $"{HostedServer.SoftwareName(Software)} {MinecraftVersion}";
}

/// <summary>
/// Importar un servidor que ya existe en otra carpeta (hecho a mano, con otro programa o de otro equipo): se reconoce
/// qué es mirando sus archivos (las librerías de Forge/NeoForge, el lanzador de Fabric/Quilt, el jar de Paper, Purpur,
/// Spigot o Vanilla) y se copia a la carpeta del Hosting. El original no se toca.
/// </summary>
public static partial class ServerImporter
{
    /// <summary>No se copian: se regeneran solos y pueden ocupar mucho.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "logs", "crash-reports", "debug" };

    /// <summary>Qué servidor hay en <paramref name="dir"/>; null si no se reconoce ninguno.</summary>
    public static ImportedServer? Detect(string dir)
    {
        if (!Directory.Exists(dir))
            return null;
        var jars = Directory.EnumerateFiles(dir, "*.jar", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).OfType<string>()
            .Where(n => !n.Contains("installer", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // NeoForge y Forge: la versión está en el nombre de su carpeta de librerías.
        if (LibraryVersions(dir, "net", "neoforged", "neoforge").FirstOrDefault() is { } neo)
        {
            var mc = MinecraftFromServerLibraries(dir) ?? MinecraftFromNeoForge(neo);
            if (mc is not null && ServerInstaller.FindForgeLaunch(dir, mc, neo, forge: false) is { } args)
                return new ImportedServer(ServerSoftware.NeoForge, mc, neo, args);
        }

        foreach (var folder in LibraryVersions(dir, "net", "minecraftforge", "forge"))
        {
            // «1.21.1-52.0.16»: versión de Minecraft y de Forge.
            var dash = folder.IndexOf('-');
            if (dash <= 0)
                continue;
            var (mc, forge) = (folder[..dash], folder[(dash + 1)..]);
            if (ServerInstaller.FindForgeLaunch(dir, mc, forge, forge: true) is { } args)
                return new ImportedServer(ServerSoftware.Forge, mc, forge, args);
        }

        // Fabric y Quilt: su lanzador (y la versión del juego en el server.jar que descarga).
        if (jars.Contains(ServerInstaller.QuiltLauncherJar, StringComparer.OrdinalIgnoreCase))
        {
            var mc = MinecraftFromJar(Path.Combine(dir, ServerInstaller.VanillaJar)) ?? MinecraftFromServerLibraries(dir);
            if (mc is not null)
                return new ImportedServer(ServerSoftware.Quilt, mc, LibraryVersions(dir, "org", "quiltmc", "quilt-loader").FirstOrDefault(),
                    ["-jar", ServerInstaller.QuiltLauncherJar]);
        }

        if (jars.Contains(ServerInstaller.FabricLauncherJar, StringComparer.OrdinalIgnoreCase))
        {
            // El lanzador de Fabric trae install.properties con las dos versiones.
            var props = ReadZipText(Path.Combine(dir, ServerInstaller.FabricLauncherJar), "install.properties") ?? string.Empty;
            var mc = Property(props, "game-version") ?? MinecraftFromJar(Path.Combine(dir, ServerInstaller.VanillaJar));
            var loader = Property(props, "fabric-loader-version") ?? LibraryVersions(dir, "net", "fabricmc", "fabric-loader").FirstOrDefault();
            if (mc is not null)
                return new ImportedServer(ServerSoftware.Fabric, mc, loader, ["-jar", ServerInstaller.FabricLauncherJar]);
        }

        // Spigot: «spigot-1.21.1.jar» (BuildTools lo llama así).
        if (jars.FirstOrDefault(j => SpigotJar().IsMatch(j)) is { } spigot)
            return new ImportedServer(ServerSoftware.Spigot, SpigotJar().Match(spigot).Groups[1].Value, null, ["-jar", spigot]);

        // Paper y Purpur: por el nombre del jar o por version_history.json (que escriben al arrancar).
        var history = VersionHistory(dir);
        foreach (var (software, name) in new[] { (ServerSoftware.Purpur, "purpur"), (ServerSoftware.Paper, "paper") })
        {
            var jar = jars.FirstOrDefault(j => j.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            var fromHistory = history?.Contains($"-{name}-", StringComparison.OrdinalIgnoreCase) == true;
            if (jar is null && !fromHistory)
                continue;
            jar ??= jars.Contains(ServerInstaller.VanillaJar, StringComparer.OrdinalIgnoreCase) ? ServerInstaller.VanillaJar : jars.FirstOrDefault();
            if (jar is null)
                continue;
            var named = NamedBuild().Match(jar);
            var mc = (named.Success ? named.Groups[1].Value : null)
                     ?? (history is not null && HistoryVersion().Match(history) is { Success: true } h ? h.Groups[2].Value : null)
                     ?? MinecraftFromJar(Path.Combine(dir, jar));
            var build = named.Success && named.Groups[2].Success ? named.Groups[2].Value
                : history is not null && HistoryVersion().Match(history) is { Success: true } b ? b.Groups[1].Value : null;
            if (mc is not null)
                return new ImportedServer(software, mc, build, ["-jar", jar]);
        }

        // Vanilla: el server.jar de Mojang (o el antiguo minecraft_server.1.x.jar).
        var vanilla = jars.FirstOrDefault(j => j.Equals(ServerInstaller.VanillaJar, StringComparison.OrdinalIgnoreCase))
                      ?? jars.FirstOrDefault(j => j.StartsWith("minecraft_server", StringComparison.OrdinalIgnoreCase));
        if (vanilla is not null && MinecraftFromJar(Path.Combine(dir, vanilla)) is { } version)
            return new ImportedServer(ServerSoftware.Vanilla, version, null, ["-jar", vanilla]);
        return null;
    }

    /// <summary>Tamaño de lo que se copiará (para avisar antes).</summary>
    public static long CopySize(string source) => Files(source).Sum(f => new FileInfo(f).Length);

    /// <summary>Copia el servidor a <paramref name="target"/> (sin logs ni informes de errores), con el progreso en bytes.</summary>
    public static async Task CopyAsync(string source, string target, IProgress<LauncherProgress>? progress, CancellationToken ct = default)
    {
        var files = Files(source).ToList();
        var total = Math.Max(1, files.Sum(f => new FileInfo(f).Length));
        long done = 0;
        var buffer = new byte[1 << 20];
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, useAsync: true))
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report(new LauncherProgress(LauncherPhase.Installing, "Copiando el servidor…", done * 100.0 / total));
                }
            }

            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file));
        }
    }

    private static IEnumerable<string> Files(string source)
    {
        foreach (var file in Directory.EnumerateFiles(source))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(source))
        {
            if (Skipped.Contains(Path.GetFileName(sub)))
                continue;
            foreach (var file in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                yield return file;
        }
    }

    // ── Pistas en los archivos ───────────────────────────────────────────

    /// <summary>Carpetas de versión de una librería (<c>libraries/net/neoforged/neoforge/&lt;versión&gt;</c>), la más nueva primero.</summary>
    private static IEnumerable<string> LibraryVersions(string dir, params string[] parts)
    {
        var path = Path.Combine([dir, "libraries", .. parts]);
        return Directory.Exists(path)
            ? Directory.EnumerateDirectories(path).Select(Path.GetFileName).OfType<string>().OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            : [];
    }

    /// <summary>Forge y NeoForge dejan el juego en <c>libraries/net/minecraft/server/&lt;mc&gt;-&lt;fecha&gt;</c>.</summary>
    private static string? MinecraftFromServerLibraries(string dir) =>
        LibraryVersions(dir, "net", "minecraft", "server").Select(v => v.Split('-')[0]).FirstOrDefault(v => v.Length > 0);

    /// <summary>NeoForge numera como el juego sin el «1.» del principio: 21.1.77 → 1.21.1; 20.4.x → 1.20.4.</summary>
    private static string? MinecraftFromNeoForge(string neo)
    {
        var parts = neo.Split('.', '-');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return null;
        return major >= 26 ? $"{major}.{minor}" : minor == 0 ? $"1.{major}" : $"1.{major}.{minor}";
    }

    /// <summary>Versión del juego que lleva un jar (su <c>version.json</c>).</summary>
    private static string? MinecraftFromJar(string jar)
    {
        if (ReadZipText(jar, "version.json") is not { } json)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? VersionHistory(string dir)
    {
        var file = Path.Combine(dir, "version_history.json");
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadZipText(string path, string entry)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry(entry) is not { } e || e.Length > 1024 * 1024)
                return null;
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Property(string text, string key) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal)) is { } line
            ? line[(key.Length + 1)..].Trim() is { Length: > 0 } value ? value : null
            : null;

    [GeneratedRegex(@"^spigot-(\d+(?:\.\d+)+)\.jar$", RegexOptions.IgnoreCase)]
    private static partial Regex SpigotJar();

    /// <summary>«paper-1.21.1-123.jar», «purpur-1.21.1-2329.jar».</summary>
    [GeneratedRegex(@"^(?:paper|purpur)-(\d+(?:\.\d+)+)(?:-(\d+))?\.jar$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedBuild();

    /// <summary><c>"currentVersion": "git-Paper-123 (MC: 1.21.1)"</c>: build y versión del juego.</summary>
    [GeneratedRegex(@"git-\w+-(\d+)[^""]*\(MC: ([0-9.]+)\)")]
    private static partial Regex HistoryVersion();
}
