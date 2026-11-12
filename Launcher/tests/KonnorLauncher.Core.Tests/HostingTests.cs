using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Tests.Infrastructure;
using KonnorLauncher.Core.Verification;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>
/// Sección Hosting: servidores alojados (lo que hacía ServerManager) y publicación del cliente para los
/// launchers (lo que hacía LauncherBackEnd).
/// </summary>
public sealed class HostingTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ── server.properties ────────────────────────────────────────────────

    [Fact]
    public void Properties_keep_comments_order_and_untouched_lines()
    {
        var file = ServerPropertiesFile.Parse("#Minecraft server properties\n#Mon Jan 01\nmotd=Hola\nserver-port=25570\nunknown-key = valor raro \n");

        file.Get("motd").ShouldBe("Hola");
        file.Port.ShouldBe(25570);
        file.Get("unknown-key").ShouldBe("valor raro ");
        file.Set("motd", "Hola");          // mismo valor: la línea no se reescribe
        file.Set("max-players", 30);       // clave nueva: al final

        file.ToString().ShouldBe("#Minecraft server properties\n#Mon Jan 01\nmotd=Hola\nserver-port=25570\nunknown-key = valor raro \nmax-players=30\n");
    }

    [Fact]
    public void Properties_escape_and_unescape_like_java()
    {
        var file = ServerPropertiesFile.Parse("motd=Servidor de Ram\\u00F3n \\u00A7aVerde\nlevel-name=mi\\ mundo\nkey\\:with\\=sep=1\ncontinued=uno \\\n    dos\n");

        file.Get("motd").ShouldBe("Servidor de Ramón §aVerde");
        file.Get("level-name").ShouldBe("mi mundo");
        file.Get("key:with=sep").ShouldBe("1");
        file.Get("continued").ShouldBe("uno dos");

        file.Set("motd", "Año ñ\\fin");
        file.ToString().ShouldContain("motd=A\\u00F1o \\u00F1\\\\fin\n");
        // Lo escrito se vuelve a leer igual.
        ServerPropertiesFile.Parse(file.ToString()).Get("motd").ShouldBe("Año ñ\\fin");
    }

    [Fact]
    public void Properties_read_utf8_and_latin1_files_and_typed_values()
    {
        ServerPropertiesFile.Parse(Encoding.UTF8.GetBytes("motd=Ñandú\n")).Get("motd").ShouldBe("Ñandú");
        ServerPropertiesFile.Parse(Encoding.Latin1.GetBytes("motd=Ñandú\n")).Get("motd").ShouldBe("Ñandú");

        var file = ServerPropertiesFile.Parse("pvp=TRUE\nwhite-list=no\nserver-port=99999\nmax-players=x\n");
        file.GetBool("pvp", false).ShouldBeTrue();
        file.GetBool("white-list", true).ShouldBeTrue(); // valor no válido: el de por defecto
        file.Port.ShouldBe(ServerPropertiesFile.DefaultPort); // fuera de rango
        file.GetInt("max-players", 20).ShouldBe(20);
    }

    [Fact]
    public void Properties_save_and_load_from_disk()
    {
        var path = _dir.Combine("server.properties");
        ServerPropertiesFile.Load(path).Keys.ShouldBeEmpty(); // no existe: vacío
        var file = new ServerPropertiesFile();
        file.Set("online-mode", false);
        file.Set("motd", "Prueba");
        file.Save(path);

        var loaded = ServerPropertiesFile.Load(path);
        loaded.GetBool("online-mode", true).ShouldBeFalse();
        loaded.Get("motd").ShouldBe("Prueba");
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    // ── Catálogo de versiones e instalador ───────────────────────────────

    [Fact]
    public void Paper_versions_skip_test_builds_and_keep_the_order()
    {
        var versions = ServerSoftwareCatalog.ParsePaperVersions("""
            {"project":{"id":"paper"},"versions":{"26.1":["26.1.2","26.1.1"],"1.21":["1.21.11","1.21.11-rc3","1.21.11-pre5","1.21.10"],"1.20":["1.20.6"]}}
            """);

        versions.ShouldBe(["26.1.2", "26.1.1", "1.21.11", "1.21.10", "1.20.6"]);
    }

    [Fact]
    public void Paper_builds_read_the_download_and_its_sha256()
    {
        var builds = ServerSoftwareCatalog.ParsePaperBuilds("""
            [{"id":231,"channel":"STABLE","downloads":{"server:default":{"name":"paper-1.21.4-231.jar","checksums":{"sha256":"aa"},"url":"https://x/231.jar"}}},
             {"id":232,"channel":"STABLE","downloads":{"server:default":{"name":"paper-1.21.4-232.jar","checksums":{"sha256":"bb"},"url":"https://x/232.jar"}}},
             {"id":233,"channel":"BETA","downloads":{}}]
            """);

        builds.Select(b => b.Build).ShouldBe(["233", "232", "231"]); // el más nuevo primero
        builds[0].Stable.ShouldBeFalse();
        builds[0].Url.ShouldBeNull();
        builds[1].ShouldBe(new ServerSoftwareCatalog.PaperBuild("232", true, "https://x/232.jar", "bb", "paper-1.21.4-232.jar"));
    }

    [Fact]
    public void Vanilla_server_download_is_read_from_the_version_json()
    {
        ServerInstaller.ReadVanillaServerDownload("""{"downloads":{"client":{"url":"c"},"server":{"sha1":"ABC","url":"https://piston/server.jar"}}}""")
            .ShouldBe(("https://piston/server.jar", "abc"));
        ServerInstaller.ReadVanillaServerDownload("""{"downloads":{"client":{"url":"c"}}}""").ShouldBe((null, null));
    }

    [Fact]
    public void Maven_release_is_read_from_its_metadata()
    {
        ServerInstaller.ReadMavenRelease("<metadata><versioning><latest>0.16.0-beta.1</latest><release>0.15.1</release><versions><version>0.15.1</version></versions></versioning></metadata>")
            .ShouldBe("0.15.1");
        ServerInstaller.ReadMavenRelease("<metadata><versioning><versions><version>1</version><version>2</version></versions></versioning></metadata>")
            .ShouldBe("2");
    }

    [Fact]
    public void Modern_forge_and_neoforge_start_with_their_arguments_file()
    {
        var forge = _dir.Combine("forge");
        Write(Path.Combine(forge, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.3.0", "win_args.txt"), "-cp x");
        Write(Path.Combine(forge, "user_jvm_args.txt"), "# -Xmx4G");
        ServerInstaller.FindForgeLaunch(forge, "1.20.1", "47.3.0", forge: true)
            .ShouldBe(["@user_jvm_args.txt", "@libraries/net/minecraftforge/forge/1.20.1-47.3.0/win_args.txt"]);

        var neo = _dir.Combine("neo");
        Write(Path.Combine(neo, "libraries", "net", "neoforged", "neoforge", "21.1.77", "win_args.txt"), "-cp x");
        ServerInstaller.FindForgeLaunch(neo, "1.21.1", "21.1.77", forge: false)
            .ShouldBe(["@libraries/net/neoforged/neoforge/21.1.77/win_args.txt"]);
    }

    [Fact]
    public void Old_forge_starts_with_its_jar_and_nothing_found_is_null()
    {
        var old = _dir.Combine("old");
        Write(Path.Combine(old, "forge-1.12.2-14.23.5.2860-installer.jar"), "x");
        Write(Path.Combine(old, "minecraft_server.1.12.2.jar"), "x");
        ServerInstaller.FindForgeLaunch(old, "1.12.2", "14.23.5.2860", forge: true).ShouldBeNull();

        Write(Path.Combine(old, "forge-1.12.2-14.23.5.2860.jar"), "x");
        ServerInstaller.FindForgeLaunch(old, "1.12.2", "14.23.5.2860", forge: true).ShouldBe(["-jar", "forge-1.12.2-14.23.5.2860.jar"]);
    }

    [Fact]
    public void Eula_is_only_accepted_when_written()
    {
        ServerInstaller.IsEulaAccepted(_dir.Path).ShouldBeFalse();
        File.WriteAllText(_dir.Combine("eula.txt"), "eula=false\n");
        ServerInstaller.IsEulaAccepted(_dir.Path).ShouldBeFalse();
        ServerInstaller.AcceptEula(_dir.Path);
        ServerInstaller.IsEulaAccepted(_dir.Path).ShouldBeTrue();
    }

    // ── Proceso del servidor ─────────────────────────────────────────────

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: Steve joined the game", "Steve", true)]
    [InlineData("[12:00:00] [Server thread/INFO]: Alex_99 left the game", "Alex_99", false)]
    [InlineData("[12:00:00] [Server thread/INFO] [minecraft/DedicatedServer]: Steve joined the game", "Steve", true)]
    [InlineData("[19:46:21] [Server thread/INFO]: System chat: hghghg joined the game", "hghghg", true)]
    [InlineData("[19:48:25] [Server thread/INFO]: System chat: hghghg left the game", "hghghg", false)]
    public void Player_join_and_leave_lines_are_recognized(string line, string name, bool joined) =>
        ServerRunner.ParsePlayerEvent(line).ShouldBe((name, joined));

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: <Steve> Bob joined the game")]
    [InlineData("[12:00:00] [Server thread/INFO]: [Not Secure] <Steve> Bob joined the game")]
    [InlineData("[12:00:00] [Server thread/INFO]: Steve joined the game, welcome")]
    [InlineData("Steve joined the game")]
    public void Chat_and_other_lines_are_not_player_events(string line) =>
        ServerRunner.ParsePlayerEvent(line).ShouldBeNull();

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: Steve[/190.104.1.2:51234] logged in with entity id 41 at (1.5, 64.0, -3.5)", "Steve", "190.104.1.2")]
    [InlineData("[12:00:00] [Server thread/INFO]: Alex_99[/127.0.0.1:60000] logged in with entity id 7 at ([world]0.5, 70.0, 0.5)", "Alex_99", "127.0.0.1")]
    [InlineData("[12:00:00] [Server thread/INFO]: Kai[/[0:0:0:0:0:0:0:1]:5555] logged in with entity id 3 at (0, 0, 0)", "Kai", "0:0:0:0:0:0:0:1")]
    [InlineData("[12:00:00] [Server thread/INFO]: Kai[/0:0:0:0:0:0:0:1:5555] logged in with entity id 3 at (0, 0, 0)", "Kai", "0:0:0:0:0:0:0:1")]
    public void Login_lines_give_the_player_address(string line, string name, string address) =>
        ServerRunner.ParseLogin(line).ShouldBe((name, address));

    [Theory]
    [InlineData("[12:00:00] [Server thread/INFO]: <Steve> Bob[/1.2.3.4:5] logged in with entity id 1")]
    [InlineData("[12:00:00] [Server thread/INFO]: Steve joined the game")]
    public void Chat_is_not_a_login_line(string line) =>
        ServerRunner.ParseLogin(line).ShouldBeNull();

    [Theory]
    [InlineData(null, 0)]
    [InlineData(-1L, 0)]
    [InlineData(0L, 5)]
    [InlineData(149L, 5)]
    [InlineData(150L, 4)]
    [InlineData(299L, 4)]
    [InlineData(300L, 3)]
    [InlineData(600L, 2)]
    [InlineData(999L, 2)]
    [InlineData(1000L, 1)]
    public void Ping_bars_follow_the_game_thresholds(long? ms, int bars) =>
        ConnectionQuality.Bars(ms).ShouldBe(bars);

    [Fact]
    public void Java_arguments_have_memory_utf8_and_nogui()
    {
        ServerRunner.BuildArguments(["-jar", "server.jar"], 4096).ShouldBe(
            ["-Xms1024M", "-Xmx4096M", "-Dfile.encoding=UTF-8", "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", "-jar", "server.jar", "nogui"]);
        ServerRunner.BuildArguments(["@args.txt"], 100)[..2].ShouldBe(["-Xms512M", "-Xmx512M"]); // mínimo 512 MB
    }

    /// <summary>
    /// Un «servidor» de prueba en Java (programa de un solo archivo): escribe Done, anuncia jugadores y se
    /// detiene con <c>stop</c>. Comprueba el ciclo completo con el Java de Mojang si está instalado.
    /// </summary>
    [Fact]
    public async Task Runner_starts_tracks_players_and_stops_with_the_stop_command()
    {
        var java = FindMojangJava();
        Assert.SkipWhen(java is null, "No hay un Java de Mojang 11+ instalado en el launcher");
        File.WriteAllText(_dir.Combine("FakeServer.java"), """
            import java.io.*;
            public class FakeServer {
                public static void main(String[] args) throws Exception {
                    System.out.println("[12:00:00] [Server thread/INFO]: Starting minecraft server version 1.21.1");
                    System.out.println("[12:00:01] [Server thread/INFO]: Done (1.234s)! For help, type \"help\"");
                    BufferedReader in = new BufferedReader(new InputStreamReader(System.in, "UTF-8"));
                    String line;
                    while ((line = in.readLine()) != null) {
                        if (line.startsWith("join ")) System.out.println("[12:00:02] [Server thread/INFO]: " + line.substring(5) + " joined the game");
                        else if (line.startsWith("leave ")) System.out.println("[12:00:03] [Server thread/INFO]: " + line.substring(6) + " left the game");
                        else if (line.equals("stop")) { System.out.println("[12:00:04] [Server thread/INFO]: Stopping server ñ"); System.exit(0); }
                    }
                }
            }
            """);

        using var runner = new ServerRunner("prueba");
        var running = new TaskCompletionSource();
        runner.StateChanged += (_, s) => { if (s == ServerState.Running) running.TrySetResult(); };
        runner.Start(java!, _dir.Path, ["FakeServer.java"], 512);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        runner.State.ShouldBe(ServerState.Running);

        runner.SendCommand("/join Steve").ShouldBeTrue();
        runner.SendCommand("join Alex").ShouldBeTrue();
        await WaitUntil(() => runner.Players.Count == 2);
        runner.SendCommand("leave Steve");
        await WaitUntil(() => runner.Players.Count == 1);
        runner.Players.ShouldBe(["Alex"]);

        await runner.StopAsync(TimeSpan.FromSeconds(20));
        runner.State.ShouldBe(ServerState.Stopped);
        runner.LastExitCode.ShouldBe(0);
        runner.Players.ShouldBeEmpty();
        runner.Lines.ShouldContain("> join Steve");
        runner.Lines.ShouldContain(l => l.Contains("Stopping server ñ")); // la consola se lee en UTF-8
        runner.SendCommand("say hola").ShouldBeFalse();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        condition().ShouldBeTrue();
    }

    private static string? FindMojangJava()
    {
        var runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KonnorLauncher", "meta", "runtime");
        if (!Directory.Exists(runtime))
            return null;
        return Directory.EnumerateFiles(runtime, "java.exe", SearchOption.AllDirectories)
            .FirstOrDefault(p => !p.Contains("jre-legacy", StringComparison.OrdinalIgnoreCase));
    }

    // ── Servidores en disco ──────────────────────────────────────────────

    [Fact]
    public void Store_creates_unique_folders_and_round_trips_the_server()
    {
        var store = new HostedServerStore(_dir.Combine("hosting"));
        var a = store.Create(new HostedServer { Name = "Mi Servidor", Software = ServerSoftware.Fabric, MinecraftVersion = "1.21.1", LoaderVersion = "0.16.9" });
        var b = store.Create(new HostedServer { Name = "Mi Servidor", Software = ServerSoftware.Paper, MinecraftVersion = "1.21.4" });

        a.Id.ShouldBe("mi-servidor");
        b.Id.ShouldBe("mi-servidor-2");
        a.LaunchArguments = ["-jar", "fabric-server-launch.jar"];
        a.Publish.Enabled = true;
        a.Publish.ClientVersion = "2.0";
        store.Save(a);

        var loaded = store.LoadAll().Single(s => s.Id == a.Id);
        loaded.Software.ShouldBe(ServerSoftware.Fabric);
        loaded.LoaderVersion.ShouldBe("0.16.9");
        loaded.LaunchArguments.ShouldBe(["-jar", "fabric-server-launch.jar"]);
        loaded.Publish.Enabled.ShouldBeTrue();
        loaded.Publish.ClientVersion.ShouldBe("2.0");
        File.ReadAllText(Path.Combine(store.GetDirectory(a.Id), HostedServerStore.FileName)).ShouldContain("\"software\": \"Fabric\"");

        store.Delete(b.Id);
        Directory.Exists(store.GetDirectory(b.Id)).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => store.Delete(".."));
        Should.Throw<ArgumentException>(() => store.Create(new HostedServer { Name = "   " }));
    }

    [Fact]
    public void Store_picks_free_ports_and_ignores_damaged_files()
    {
        var store = new HostedServerStore(_dir.Combine("hosting"));
        store.NextFreeGamePort().ShouldBe(25565);
        var a = store.Create(new HostedServer { Name = "Uno" });
        var props = new ServerPropertiesFile();
        props.Set("server-port", 25565);
        props.Save(Path.Combine(store.GetDirectory(a.Id), ServerPropertiesFile.FileName));
        store.NextFreeGamePort().ShouldBe(25566);
        store.NextFreePublishPort().ShouldBe(8001);           // «Uno» usa el 8000 por defecto
        store.NextFreePublishPort(exceptId: a.Id).ShouldBe(8000);

        Directory.CreateDirectory(_dir.Combine("hosting", "roto"));
        File.WriteAllText(_dir.Combine("hosting", "roto", HostedServerStore.FileName), "{ no es json");
        store.LoadAll().Select(s => s.Id).ShouldBe([a.Id]);
    }

    [Fact]
    public void Client_loader_and_content_folder_depend_on_the_software()
    {
        new HostedServer { Software = ServerSoftware.Paper }.ClientLoader.ShouldBe(ModLoader.Vanilla);
        new HostedServer { Software = ServerSoftware.Paper }.ContentFolder.ShouldBe("plugins");
        new HostedServer { Software = ServerSoftware.NeoForge }.ClientLoader.ShouldBe(ModLoader.NeoForge);
        new HostedServer { Software = ServerSoftware.Quilt }.ContentFolder.ShouldBe("mods");
        new HostedServer { Software = ServerSoftware.Vanilla }.ContentFolder.ShouldBeNull();
        new HostedServer { Software = ServerSoftware.Vanilla }.IsModded.ShouldBeFalse();
    }

    // ── Publicación del cliente (LauncherBackEnd) ────────────────────────

    private (HostedServer Server, string ServerDir, string ClientDir, string InternalDir) CreatePublishedServer(ServerSoftware software = ServerSoftware.Fabric)
    {
        var server = new HostedServer
        {
            Id = "srv", Name = "Srv", Software = software, MinecraftVersion = "1.21.1", LoaderVersion = software == ServerSoftware.Fabric ? "0.16.9" : null,
            Publish = new ClientPublishSettings { ClientVersion = "1.2.0", Port = FreePort(), AlwaysOnline = true },
        };
        var serverDir = _dir.Combine("srv");
        var clientDir = Path.Combine(serverDir, HostedServerStore.ClientFolder);
        Write(Path.Combine(serverDir, "mods", "sodium.jar"), "mod del servidor");
        Write(Path.Combine(serverDir, "mods", "lithium.jar"), "otro mod");
        Write(Path.Combine(serverDir, "world", "level.dat"), "mundo");             // nunca se publica
        Write(Path.Combine(clientDir, "mods", "lithium.jar"), "versión del cliente"); // reemplaza la del servidor
        Write(Path.Combine(clientDir, "config", "sodium.json"), "{}");
        Write(Path.Combine(clientDir, "resourcepacks", "pack.zip"), "pack");
        Write(Path.Combine(clientDir, "versions", "x", "x.json"), "nunca");      // lo instala el launcher
        Write(Path.Combine(clientDir, "Thumbs.db"), "basura");
        return (server, serverDir, clientDir, Path.Combine(serverDir, HostedServerStore.InternalFolder));
    }

    [Fact]
    public void Manifest_has_the_game_policy_and_hashes_the_launcher_expects()
    {
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer();
        var manifestPath = Path.Combine(internalDir, "client_manifest.json");

        var result = ClientManifestBuilder.Build(server, serverDir, clientDir, manifestPath, new HashCache(Path.Combine(internalDir, "hashes.json")));

        result.Changed.ShouldBeTrue();
        var manifest = ManifestParser.Parse(File.ReadAllText(manifestPath)); // lo acepta el verificador del launcher
        manifest.ClientVersion.ShouldBe("1.2.0");
        manifest.Game.ShouldBe(new ManifestGame("1.21.1", ModLoader.Fabric, "0.16.9"));
        manifest.AlwaysOnline.ShouldBe(true);
        manifest.DeleteUnauthorized.ShouldBe(true);
        manifest.AssetBlocks.ShouldBeEmpty();
        manifest.Files.Select(f => f.UrlPath).Order().ShouldBe(["config/sodium.json", "mods/lithium.jar", "mods/sodium.jar", "resourcepacks/pack.zip"]);

        var lithium = manifest.Files.Single(f => f.UrlPath == "mods/lithium.jar");
        lithium.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("versión del cliente"))));
        lithium.Sha512.ShouldBe(Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes("versión del cliente"))));
        manifest.Files.Single(f => f.UrlPath == "config/sodium.json").Sha512.ShouldBeNull(); // solo mods, resourcepacks y shaderpacks

        // Sin cambios no se reescribe; la caché evita volver a leer los archivos.
        var cache = new HashCache(Path.Combine(internalDir, "hashes.json"));
        ClientManifestBuilder.Build(server, serverDir, clientDir, manifestPath, cache).Changed.ShouldBeFalse();
        cache.Misses.ShouldBe(0);
        cache.Hits.ShouldBe(4);
    }

    [Fact]
    public void Paper_publishes_vanilla_and_server_mods_can_be_left_out()
    {
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer(ServerSoftware.Paper);
        var manifestPath = Path.Combine(internalDir, "client_manifest.json");

        ClientManifestBuilder.Build(server, serverDir, clientDir, manifestPath, new HashCache(Path.Combine(internalDir, "h.json")));
        var manifest = ManifestParser.Parse(File.ReadAllText(manifestPath));
        manifest.Game.ShouldBe(new ManifestGame("1.21.1", ModLoader.Vanilla, null));
        manifest.Files.ShouldNotContain(f => f.UrlPath == "mods/sodium.jar"); // Paper no publica la carpeta mods del servidor

        server.Software = ServerSoftware.Fabric;
        server.LoaderVersion = "0.16.9";
        server.Publish.IncludeServerMods = false;
        ClientManifestBuilder.Build(server, serverDir, clientDir, manifestPath, new HashCache(Path.Combine(internalDir, "h.json")));
        ManifestParser.Parse(File.ReadAllText(manifestPath)).Files.ShouldNotContain(f => f.UrlPath == "mods/sodium.jar");
    }

    [Fact]
    public void Names_the_launcher_would_reject_are_left_out_and_reported()
    {
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer();
        // Windows quita el punto final al crear un archivo, salvo con el prefijo \?\ (así llegan desde otras herramientas).
        File.WriteAllText(@"\\?\" + Path.Combine(clientDir, "config", "termina en punto."), "x");

        var result = ClientManifestBuilder.Build(server, serverDir, clientDir, Path.Combine(internalDir, "m.json"), new HashCache(Path.Combine(internalDir, "h.json")));

        result.InvalidPaths.ShouldBe(["config/termina en punto."]);
        result.PhysicalFiles.Keys.ShouldNotContain("config/termina en punto.");
        File.Delete(@"\\?\" +Path.Combine(clientDir, "config", "termina en punto.")); // la carpeta temporal se puede borrar
    }

    /// <summary>
    /// De punta a punta: el servidor publica con Kestrel y el verificador del launcher (el mismo que usa
    /// «Sincronizar con el servidor») descarga exactamente esos archivos en una instancia.
    /// </summary>
    [Fact]
    public async Task Launcher_verifier_syncs_an_instance_from_the_published_client()
    {
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer();
        await using var publisher = new ClientPublisher();
        await publisher.StartAsync(server, serverDir, clientDir, internalDir, TestContext.Current.CancellationToken);
        publisher.IsRunning.ShouldBeTrue();
        var baseUrl = $"http://127.0.0.1:{server.Publish.Port}";

        using var http = new HttpClient();
        (await http.GetStringAsync(baseUrl + "/", TestContext.Current.CancellationToken)).ShouldContain("\"status\":\"online\"");

        // Una instancia con un mod que sobra: se borra (delete_unauthorized).
        var gameDir = _dir.Combine("instancia");
        Write(Path.Combine(gameDir, "mods", "sobra.jar"), "no autorizado");
        var verifier = new ClientVerifier(http) { RetryDelay = TimeSpan.FromMilliseconds(10) };
        var result = await verifier.VerifyAsync(
            new VerifierOptions { GameDir = gameDir, ServerUrl = baseUrl, EnableVerify = true, DeleteUnauthorized = true },
            null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(gameDir, "mods", "sodium.jar")).ShouldBe("mod del servidor");
        File.ReadAllText(Path.Combine(gameDir, "mods", "lithium.jar")).ShouldBe("versión del cliente");
        File.ReadAllText(Path.Combine(gameDir, "resourcepacks", "pack.zip")).ShouldBe("pack");
        File.Exists(Path.Combine(gameDir, "mods", "sobra.jar")).ShouldBeFalse();
        publisher.Activity.Verifications.ShouldBeGreaterThanOrEqualTo(1);
        publisher.Activity.FilesServed.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task Only_files_in_the_manifest_are_served()
    {
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer();
        await using var publisher = new ClientPublisher();
        await publisher.StartAsync(server, serverDir, clientDir, internalDir, TestContext.Current.CancellationToken);
        var baseUrl = $"http://127.0.0.1:{server.Publish.Port}";
        using var http = new HttpClient();

        (await http.GetAsync(baseUrl + "/client/mods/sodium.jar", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var path in new[] { "/client/Thumbs.db", "/client/versions/x/x.json", "/client/..%2F..%2Fworld%2Flevel.dat", "/client/../world/level.dat",
                     "/client/../konnor-server.json", "/client/.konnor/hashes.json" })
            (await http.GetAsync(baseUrl + path, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound, path);

        // Rangos: el launcher puede reanudar descargas.
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/client/resourcepacks/pack.zip");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 2);
        var partial = await http.SendAsync(request, TestContext.Current.CancellationToken);
        partial.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("ac");
    }

    [Fact]
    public async Task Changes_in_the_client_folder_regenerate_the_manifest()
    {
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer();
        await using var publisher = new ClientPublisher();
        await publisher.StartAsync(server, serverDir, clientDir, internalDir, TestContext.Current.CancellationToken);
        publisher.Current!.FileCount.ShouldBe(4);

        Write(Path.Combine(clientDir, "shaderpacks", "nuevo.zip"), "shader");
        for (var i = 0; i < 200 && publisher.Current!.FileCount != 5; i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);

        publisher.Current!.FileCount.ShouldBe(5);
        publisher.Current.PhysicalFiles.Keys.ShouldContain("shaderpacks/nuevo.zip");
    }

    [Fact]
    public async Task A_busy_port_gives_a_readable_error()
    {
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer();
        using var listener = TcpListener.Create(server.Publish.Port); // IPv4 e IPv6, como otro programa en ese puerto
        listener.Start();
        await using var publisher = new ClientPublisher();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            publisher.StartAsync(server, serverDir, clientDir, internalDir, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain($"El puerto {server.Publish.Port} está en uso");
        publisher.IsRunning.ShouldBeFalse();
        NetworkAccess.IsPortFree(server.Publish.Port).ShouldBeFalse();
    }

    // ── Unirse automáticamente, paquete obligatorio y jugadores ──────────

    [Fact]
    public void Auto_join_is_published_and_read_by_the_launcher()
    {
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer();
        var manifestPath = Path.Combine(internalDir, "m.json");
        server.Publish.JoinAddress = "mi-servidor.com:25570";

        ClientManifestBuilder.Build(server, serverDir, clientDir, manifestPath, new HashCache(Path.Combine(internalDir, "h.json")));
        ManifestParser.Parse(File.ReadAllText(manifestPath)).AutoJoinAddress.ShouldBe("mi-servidor.com:25570");

        server.Publish.AutoJoin = false; // desactivado: no se publica
        ClientManifestBuilder.Build(server, serverDir, clientDir, manifestPath, new HashCache(Path.Combine(internalDir, "h.json")));
        var text = File.ReadAllText(manifestPath);
        text.ShouldContain("\"auto_join\": false");
        text.ShouldNotContain("server_address");
        ManifestParser.Parse(text).AutoJoinAddress.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{ "auto_join": true, "server_address": "Play.Example.com", "files": {} }""", "play.example.com")]
    [InlineData("""{ "auto_join": true, "server_address": "1.2.3.4:25570", "files": {} }""", "1.2.3.4:25570")]
    [InlineData("""{ "auto_join": true, "server_address": "http://mal", "files": {} }""", null)]
    [InlineData("""{ "auto_join": true, "files": {} }""", null)]
    [InlineData("""{ "auto_join": false, "server_address": "a.com", "files": {} }""", null)]
    [InlineData("""{ "server_address": "a.com", "files": {} }""", null)]
    public void Launcher_reads_auto_join_only_with_a_valid_address(string json, string? expected) =>
        ManifestParser.Parse(json).AutoJoinAddress.ShouldBe(expected);

    [Fact]
    public void Auto_join_is_part_of_the_instance_policy()
    {
        var a = new GameInstance { ServerAutoJoin = null };
        var b = a.Clone();
        b.ServerAutoJoin = "a.com";
        InstanceSync.RulesDiffer(a, b).ShouldBeTrue();
        InstanceSync.CopyPolicy(b, a);
        a.ServerAutoJoin.ShouldBe("a.com");
    }

    [Fact]
    public void Required_pack_is_set_in_server_properties_and_removed_when_disabled()
    {
        var (server, serverDir, clientDir, _) = CreatePublishedServer();
        var props = Path.Combine(serverDir, ServerPropertiesFile.FileName);
        Write(Path.Combine(clientDir, "resourcepacks", "z.zip"), "otro");
        server.Publish.Enabled = true;
        server.Publish.ProtectPacks = true;

        ServerResourcePack.Apply(server, serverDir, clientDir, "http://mi-servidor.com:8000").ShouldContain("pack.zip"); // el primero por nombre
        var file = ServerPropertiesFile.Load(props);
        file.Get("resource-pack").ShouldBe("http://mi-servidor.com:8000/client/resourcepacks/pack.zip");
        file.Get("resource-pack-sha1").ShouldBe(Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("pack"))));
        file.GetBool("require-resource-pack", false).ShouldBeTrue();
        server.Publish.ManagedResourcePackUrl.ShouldBe(file.Get("resource-pack"));

        server.Publish.ProtectPacks = false;
        ServerResourcePack.Apply(server, serverDir, clientDir, "http://mi-servidor.com:8000").ShouldBeNull();
        file = ServerPropertiesFile.Load(props);
        file.Get("resource-pack").ShouldBe(string.Empty);
        file.GetBool("require-resource-pack", true).ShouldBeFalse();
        server.Publish.ManagedResourcePackUrl.ShouldBeNull();
    }

    [Fact]
    public void A_pack_set_by_hand_is_respected_and_no_pack_is_reported()
    {
        var (server, serverDir, clientDir, _) = CreatePublishedServer();
        var props = Path.Combine(serverDir, ServerPropertiesFile.FileName);
        server.Publish.Enabled = true;
        server.Publish.ProtectPacks = true;
        var manual = new ServerPropertiesFile();
        manual.Set("resource-pack", "https://cdn.example.com/pack.zip");
        manual.Save(props);

        ServerResourcePack.Apply(server, serverDir, clientDir, "http://x:8000").ShouldContain("se respeta");
        ServerPropertiesFile.Load(props).Get("resource-pack").ShouldBe("https://cdn.example.com/pack.zip");

        server.Publish.ProtectPacks = false; // no es el del launcher: no se quita
        ServerResourcePack.Apply(server, serverDir, clientDir, "http://x:8000");
        ServerPropertiesFile.Load(props).Get("resource-pack").ShouldBe("https://cdn.example.com/pack.zip");

        File.Delete(props);
        Directory.Delete(Path.Combine(clientDir, "resourcepacks"), recursive: true);
        server.Publish.ProtectPacks = true;
        ServerResourcePack.Apply(server, serverDir, clientDir, "http://x:8000").ShouldContain("pon un paquete .zip");
    }

    [Fact]
    public void Player_history_lists_who_joined_from_playerdata_and_usercache()
    {
        var dir = _dir.Combine("historial");
        var steve = Guid.NewGuid();
        var alex = Guid.NewGuid();
        var neverJoined = Guid.NewGuid();
        PlayerHistory.Load(dir).ShouldBeEmpty(); // sin mundo

        Write(Path.Combine(dir, "world", "playerdata", steve + ".dat"), "x");
        Write(Path.Combine(dir, "world", "playerdata", alex + ".dat"), "x");
        Write(Path.Combine(dir, "world", "playerdata", Guid.NewGuid() + ".dat"), "x"); // sin nombre conocido
        Write(Path.Combine(dir, "world", "playerdata", steve + ".dat_old"), "x");
        Write(Path.Combine(dir, "usercache.json"), $$"""
            [{"name":"Steve","uuid":"{{steve}}","expiresOn":"x"},{"name":"alex","uuid":"{{alex}}"},{"name":"Notch","uuid":"{{neverJoined}}"}]
            """);

        PlayerHistory.Load(dir).ShouldBe(["alex", "Steve"]); // Notch solo se buscó (p. ej. con /op): no entró

        // Desde la 26.1 los datos de jugador están en players/data.
        var nuevo = Guid.NewGuid();
        Write(Path.Combine(dir, "world", "players", "data", nuevo + ".dat"), "x");
        Write(Path.Combine(dir, "usercache.json"), $$"""
            [{"name":"Steve","uuid":"{{steve}}"},{"name":"alex","uuid":"{{alex}}"},{"name":"Nuevo","uuid":"{{nuevo}}"}]
            """);
        PlayerHistory.Load(dir).ShouldBe(["alex", "Nuevo", "Steve"]);

        Write(Path.Combine(dir, "usercache.json"), "no es json");
        PlayerHistory.Load(dir).ShouldBeEmpty();
    }

    // ── Mods solo de servidor y RAM recomendada ──────────────────────────

    [Fact]
    public async Task Server_only_mods_are_not_sent_to_players()
    {
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer();
        Write(Path.Combine(serverDir, "mods", "admin.jar"), "solo servidor");
        Write(Path.Combine(serverDir, "mods", "local.jar"), "no está en Modrinth");
        Write(Path.Combine(serverDir, "mods", "forzado.jar"), "solo servidor también");
        Write(Path.Combine(clientDir, "mods", "forzado.jar"), "solo servidor también"); // lo pone el administrador: se envía
        var projects = new Dictionary<string, (string Id, string Side)>
        {
            [Sha512Hex("mod del servidor")] = ("sodium", "required"),
            [Sha512Hex("solo servidor")] = ("admin", "unsupported"),
            [Sha512Hex("solo servidor también")] = ("forzado", "unsupported"),
        };
        var requests = 0;
        var down = false;
        using var http = new HttpClient(new RouteHandler(request =>
        {
            requests++;
            if (down)
                throw new HttpRequestException("sin conexión");
            if (request.RequestUri!.AbsolutePath.EndsWith("/version_files"))
                return System.Text.Json.JsonSerializer.Serialize(projects.ToDictionary(p => p.Key, p => new { id = "v-" + p.Value.Id, project_id = p.Value.Id }));
            return System.Text.Json.JsonSerializer.Serialize(projects.Values.Select(p => new { id = p.Id, client_side = p.Side, server_side = "required" }));
        }));
        var modrinth = new KonnorLauncher.Core.Modrinth.ModrinthClient(new Factory(http), baseUrl: "https://api.example.test/v2");
        var cache = new HashCache(Path.Combine(internalDir, "h.json"));
        var sidesFile = Path.Combine(internalDir, "mod-sides.json");

        var skipped = await ServerModFilter.FindServerOnlyAsync(Path.Combine(serverDir, "mods"), cache, sidesFile, modrinth,
            ct: TestContext.Current.CancellationToken);
        skipped.Order().ShouldBe(["admin.jar", "forzado.jar"]);
        var result = ClientManifestBuilder.Build(server, serverDir, clientDir, Path.Combine(internalDir, "m.json"), cache, skipped);
        result.PhysicalFiles.Keys.Where(k => k.StartsWith("mods/")).Order()
            .ShouldBe(["mods/forzado.jar", "mods/lithium.jar", "mods/local.jar", "mods/sodium.jar"]);
        result.PhysicalFiles["mods/forzado.jar"].ShouldStartWith(clientDir);
        result.SkippedServerMods.ShouldBe(skipped);

        // Lo ya consultado se guarda: sin Modrinth se sigue omitiendo, y un mod nuevo se envía (no se puede saber).
        var before = requests;
        down = true;
        (await ServerModFilter.FindServerOnlyAsync(Path.Combine(serverDir, "mods"), cache, sidesFile, modrinth,
            ct: TestContext.Current.CancellationToken)).Order().ShouldBe(["admin.jar", "forzado.jar"]);
        requests.ShouldBe(before);
        Write(Path.Combine(serverDir, "mods", "nuevo.jar"), "otro solo servidor");
        (await ServerModFilter.FindServerOnlyAsync(Path.Combine(serverDir, "mods"), cache, sidesFile, modrinth,
            ct: TestContext.Current.CancellationToken)).ShouldNotContain("nuevo.jar");
    }

    [Fact]
    public void Recommended_memory_is_published_and_used_until_the_player_picks_one()
    {
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer();
        var manifestPath = Path.Combine(internalDir, "m.json");
        ClientManifestBuilder.Build(server, serverDir, clientDir, manifestPath, new HashCache(Path.Combine(internalDir, "h.json")));
        File.ReadAllText(manifestPath).ShouldNotContain("recommended_memory_mb"); // desactivada: no se publica
        ManifestParser.Parse(File.ReadAllText(manifestPath)).VerifyHashes.ShouldBe(false); // seguridad avanzada: desactivada por defecto
        server.Publish.VerifyHashes = true;

        server.Publish.RecommendedMemoryMb = 6144;
        ClientManifestBuilder.Build(server, serverDir, clientDir, manifestPath, new HashCache(Path.Combine(internalDir, "h.json")));
        ManifestParser.Parse(File.ReadAllText(manifestPath)).RecommendedMemoryMb.ShouldBe(6144);
        ManifestParser.Parse(File.ReadAllText(manifestPath)).VerifyHashes.ShouldBe(true);

        var instance = new GameInstance { SyncWithServer = true, ServerMemoryMb = 6144 };
        instance.ResolveMemoryMb(4096).ShouldBe(Math.Min(6144, KonnorLauncher.Core.Configuration.SystemMemory.MaxMb));
        instance.MemoryMb = 3072; // la elige el jugador
        instance.ResolveMemoryMb(4096).ShouldBe(3072);
        new GameInstance { SyncWithServer = false, ServerMemoryMb = 6144 }.ResolveMemoryMb(4096).ShouldBe(4096);

        var other = new GameInstance();
        InstanceSync.RulesDiffer(other, instance).ShouldBeTrue();
        InstanceSync.CopyPolicy(instance, other);
        other.ServerMemoryMb.ShouldBe(6144);
        other.MemoryMb.ShouldBeNull(); // la del jugador no se toca
    }

    [Theory]
    [InlineData("""{ "recommended_memory_mb": 8192, "files": {} }""", 8192)]
    [InlineData("""{ "recommended_memory_mb": "8192", "files": {} }""", null)]
    [InlineData("""{ "recommended_memory_mb": 100, "files": {} }""", null)]
    [InlineData("""{ "recommended_memory_mb": 6.5, "files": {} }""", null)]
    [InlineData("""{ "files": {} }""", null)]
    public void Launcher_ignores_an_invalid_recommended_memory(string json, int? expected) =>
        ManifestParser.Parse(json).RecommendedMemoryMb.ShouldBe(expected);

    /// <summary>
    /// De punta a punta con firma: el launcher consulta las reglas al servidor publicado (Kestrel), fija su clave y
    /// guarda la sesión firmada; al reiniciar, el servidor conserva la clave y el launcher lo sigue aceptando.
    /// </summary>
    [Fact]
    public async Task Launcher_gets_the_signed_rules_from_the_published_server()
    {
        var ct = TestContext.Current.CancellationToken;
        var (server, serverDir, clientDir, internalDir) = CreatePublishedServer();
        server.Publish.AlwaysOnline = false;
        var baseUrl = $"http://127.0.0.1:{server.Publish.Port}";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var sync = new InstanceSync(new Factory(http), metaDir: _dir.Combine("meta"));
        var instance = new GameInstance { Id = "srv", Name = "Srv", SyncWithServer = true, ServerUrl = baseUrl };

        string pinned;
        await using (var publisher = new ClientPublisher())
        {
            await publisher.StartAsync(server, serverDir, clientDir, internalDir, ct);
            var result = await sync.SyncAsync(instance, _dir.Combine("instances"), null, ct);

            result.Success.ShouldBeTrue(result.Message);
            instance.SessionStatus.ShouldBe(KonnorLauncher.Core.Verification.PolicyStatus.Valid);
            instance.ServerAlwaysOnline.ShouldBeFalse();
            pinned = instance.ServerKey.ShouldNotBeNull();
            File.Exists(Path.Combine(internalDir, ClientPublisher.KeyFileName)).ShouldBeTrue();
        }

        // El servidor se reinicia con la misma clave y cambia una regla: el launcher la acepta.
        server.Publish.AlwaysOnline = true;
        await using var restarted = new ClientPublisher();
        await restarted.StartAsync(server, serverDir, clientDir, internalDir, ct);
        (await sync.RefreshPolicyAsync(instance, ct)).ShouldBeTrue();
        instance.ServerKey.ShouldBe(pinned);
        instance.ServerAlwaysOnline.ShouldBeTrue();
    }

    private static string Sha512Hex(string text) => Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Factory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
            });
    }

    // ── Utilidades ───────────────────────────────────────────────────────

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
