using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Modrinth;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Búsqueda e instalación de contenido de Modrinth (contra un servidor HTTP local).</summary>
public sealed class ModrinthTests : IAsyncLifetime
{
    private readonly LocalHttpServer _server = new();
    private readonly TempDir _dir = new();
    private HttpClient _http = null!;
    private InstanceStore _store = null!;

    public ValueTask InitializeAsync()
    {
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _store = new InstanceStore(_dir.Combine("instances"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        _dir.Dispose();
    }

    private sealed class Factory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private ContentInstaller CreateInstaller() => new(new ModrinthClient(new Factory(_http), baseUrl: _server.BaseUrl), _store);

    private static string Escaped(IEnumerable<string> values) => Uri.EscapeDataString(JsonSerializer.Serialize(values.ToList()));

    private static string VersionsPath(string project, string[]? loaders, string? game)
    {
        var query = new List<string>();
        if (loaders is not null)
            query.Add("loaders=" + Escaped(loaders));
        if (game is not null)
            query.Add("game_versions=" + Escaped([game]));
        query.Add("include_changelog=false");
        return $"/project/{project}/version?" + string.Join('&', query);
    }

    /// <summary>Publica un archivo y devuelve su entrada de versión (con SHA-512 o SHA-1).</summary>
    private object File(string name, byte[] content, bool sha1 = false, string? servedAs = null)
    {
        _server.Map("/files/" + (servedAs ?? name), content);
        return new
        {
            url = $"{_server.BaseUrl}/files/{servedAs ?? name}",
            filename = name,
            primary = true,
            size = content.Length,
            hashes = sha1
                ? (object)new { sha1 = Convert.ToHexStringLower(SHA1.HashData(content)) }
                : new { sha512 = Convert.ToHexStringLower(SHA512.HashData(content)) },
        };
    }

    private static object Version(string id, string project, object file, string type = "release", object[]? deps = null) => new
    {
        id,
        project_id = project,
        name = id,
        version_number = id,
        version_type = type,
        game_versions = new[] { "1.21.1" },
        loaders = new[] { "fabric" },
        date_published = DateTimeOffset.UtcNow,
        files = new[] { file },
        dependencies = deps ?? [],
    };

    private void MapJson(string path, object value) => _server.Map(path, JsonSerializer.Serialize(value));

    private GameInstance FabricInstance() =>
        _store.Create(new GameInstance { Name = "Fabric", MinecraftVersion = "1.21.1", Loader = ModLoader.Fabric, LoaderVersion = "0.16.10" });

    private string ModsDir(GameInstance instance) => Path.Combine(_store.GetDirectory(instance.Id), "mods");

    // ── Búsqueda ─────────────────────────────────────────────────────────

    [Fact]
    public void Search_facets_combine_groups_with_and_and_options_with_or()
    {
        var query = new SearchQuery
        {
            Type = ContentType.Mod,
            IncludedCategories = ["optimization"],
            ExcludedCategories = ["magic"],
            GameVersions = ["1.21.1", "1.20.1"],
            Loaders = ["fabric", "quilt"],
            Environments = [ProjectEnvironment.Client],
            OpenSourceOnly = true,
            Page = 3,
            Limit = 20,
        };

        var facets = query.BuildFacets();

        facets.ShouldBe(
        [
            ["project_type:mod"],
            ["categories:optimization"],
            ["categories!=magic"],
            ["versions:1.21.1", "versions:1.20.1"],
            ["categories:fabric", "categories:quilt"],
            ["client_side:required", "client_side:optional"],
            ["open_source:true"],
        ]);
        query.Offset.ShouldBe(40);
        query.ToQueryString().ShouldContain("index=relevance");
    }

    [Fact]
    public async Task Search_results_are_read_from_the_api()
    {
        var query = new SearchQuery { Type = ContentType.Shader, Text = "sombras", Sort = SearchSort.Downloads, Limit = 5 };
        MapJson("/search?" + query.ToQueryString(), new
        {
            hits = new[] { new { project_id = "abc", project_type = "shader", title = "Sombras", author = "yo", downloads = 1234, follows = 5,
                date_modified = "2026-10-01T00:00:00Z", categories = new[] { "iris" }, display_categories = new[] { "shadows" } } },
            offset = 0, limit = 5, total_hits = 1,
        });

        var page = await new ModrinthClient(new Factory(_http), baseUrl: _server.BaseUrl).SearchAsync(query, TestContext.Current.CancellationToken);

        page.TotalHits.ShouldBe(1);
        page.Hits[0].Title.ShouldBe("Sombras");
        page.Hits[0].DisplayCategories.ShouldBe(["shadows"]);
    }

    // ── Mods ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Installing_a_mod_brings_its_required_dependencies_and_remembers_them()
    {
        var instance = FabricInstance();
        MapJson(VersionsPath("modA", ["fabric"], "1.21.1"), new[]
        {
            Version("a-beta", "modA", File("a-beta.jar", "beta"u8.ToArray()), type: "beta"),
            Version("a1", "modA", File("a.jar", "mod A"u8.ToArray()),
                deps: [new { project_id = "libB", dependency_type = "required" }, new { project_id = "optC", dependency_type = "optional" }]),
        });
        MapJson(VersionsPath("libB", ["fabric"], "1.21.1"), new[] { Version("b1", "libB", File("b.jar", "lib B"u8.ToArray(), sha1: true)) });

        var result = await CreateInstaller().InstallAsync(instance, "modA", ContentType.Mod, ct: TestContext.Current.CancellationToken);

        result.FileName.ShouldBe("a.jar"); // la estable, aunque la beta sea más reciente
        result.Dependencies.ShouldBe(["b.jar"]);
        System.IO.File.ReadAllText(Path.Combine(ModsDir(instance), "b.jar")).ShouldBe("lib B");
        System.IO.File.Exists(Path.Combine(ModsDir(instance), "a-beta.jar")).ShouldBeFalse();
        ContentInstaller.InstalledProjects(_store.GetDirectory(instance.Id)).ShouldBe(["modA", "libB"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_new_version_replaces_the_one_installed_before()
    {
        var instance = FabricInstance();
        MapJson(VersionsPath("modA", ["fabric"], "1.21.1"), new[] { Version("a1", "modA", File("a-1.0.jar", "uno"u8.ToArray())) });
        await CreateInstaller().InstallAsync(instance, "modA", ContentType.Mod, ct: TestContext.Current.CancellationToken);

        MapJson(VersionsPath("modA", ["fabric"], "1.21.1"), new[] { Version("a2", "modA", File("a-2.0.jar", "dos"u8.ToArray())) });
        await CreateInstaller().InstallAsync(instance, "modA", ContentType.Mod, ct: TestContext.Current.CancellationToken);

        Directory.GetFiles(ModsDir(instance)).Select(Path.GetFileName).ShouldBe(["a-2.0.jar"]);
    }

    [Fact]
    public async Task Downloads_with_a_wrong_hash_or_unsafe_name_are_rejected()
    {
        var instance = FabricInstance();
        var good = File("ok.jar", "bueno"u8.ToArray());
        _server.Map("/files/ok.jar", "cambiado por el camino"u8.ToArray()); // el contenido ya no coincide con el hash
        MapJson(VersionsPath("modA", ["fabric"], "1.21.1"), new[] { Version("a1", "modA", good) });

        await Should.ThrowAsync<ModrinthException>(() => CreateInstaller().InstallAsync(instance, "modA", ContentType.Mod, ct: TestContext.Current.CancellationToken));
        Directory.GetFiles(ModsDir(instance)).ShouldBeEmpty(); // ni el archivo ni su temporal

        MapJson(VersionsPath("modB", ["fabric"], "1.21.1"), new[] { Version("b1", "modB", File("../fuera.jar", "x"u8.ToArray(), servedAs: "fuera.jar")) });
        await Should.ThrowAsync<ModrinthException>(() => CreateInstaller().InstallAsync(instance, "modB", ContentType.Mod, ct: TestContext.Current.CancellationToken));
        System.IO.File.Exists(Path.Combine(_store.GetDirectory(instance.Id), "fuera.jar")).ShouldBeFalse();
    }

    [Fact]
    public void Synced_and_vanilla_instances_do_not_accept_mods()
    {
        var synced = new GameInstance { SyncWithServer = true, MinecraftVersion = "fabric-loader-0.16.10-1.21.1" };
        ContentInstaller.BlockReason(synced, ContentType.Mod).ShouldNotBeNull();
        ContentInstaller.BlockReason(synced, ContentType.ResourcePack).ShouldBeNull(); // packs: solo si el servidor los protege
        ContentInstaller.BlockReason(new GameInstance { SyncWithServer = true, ServerProtectsPacks = true }, ContentType.Shader).ShouldNotBeNull();
        ContentInstaller.BlockReason(new GameInstance { MinecraftVersion = "1.21.1" }, ContentType.Mod).ShouldNotBeNull();
        ContentInstaller.BlockReason(new GameInstance { MinecraftVersion = "1.21.1" }, ContentType.Shader).ShouldBeNull();

        // Quilt también acepta mods de Fabric; la versión de una sincronizada sale del id del servidor.
        ContentInstaller.CompatibleLoaders(new GameInstance { Loader = ModLoader.Quilt }, ContentType.Mod).ShouldBe(["quilt", "fabric"]);
        ContentInstaller.GameVersion(synced).ShouldBe("1.21.1");
    }

    [Fact]
    public async Task Data_packs_go_into_the_chosen_world()
    {
        var instance = FabricInstance();
        var world = Path.Combine(_store.GetDirectory(instance.Id), "saves", "Mi mundo");
        Directory.CreateDirectory(world);
        System.IO.File.WriteAllText(Path.Combine(world, "level.dat"), "x");
        MapJson(VersionsPath("dp", ["datapack"], "1.21.1"), new[] { Version("d1", "dp", File("pack.zip", "datos"u8.ToArray())) });

        ContentInstaller.Worlds(_store.GetDirectory(instance.Id)).ShouldBe(["Mi mundo"]);
        await CreateInstaller().InstallAsync(instance, "dp", ContentType.DataPack, "Mi mundo", ct: TestContext.Current.CancellationToken);

        System.IO.File.Exists(Path.Combine(world, "datapacks", "pack.zip")).ShouldBeTrue();
        await Should.ThrowAsync<ModrinthException>(() =>
            CreateInstaller().InstallAsync(instance, "dp", ContentType.DataPack, "..", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_chosen_version_is_installed_even_if_it_is_not_the_latest()
    {
        var instance = FabricInstance();
        MapJson("/version/viejo", Version("viejo", "modA", File("a-0.9.jar", "antigua"u8.ToArray())));
        MapJson("/version/ajeno", Version("ajeno", "otro", File("otro.jar", "x"u8.ToArray())));

        var result = await CreateInstaller().InstallAsync(instance, "modA", ContentType.Mod, versionId: "viejo", ct: TestContext.Current.CancellationToken);

        result.FileName.ShouldBe("a-0.9.jar");
        await Should.ThrowAsync<ModrinthException>(() => // una versión de otro proyecto no se instala con este
            CreateInstaller().InstallAsync(instance, "modA", ContentType.Mod, versionId: "ajeno", ct: TestContext.Current.CancellationToken));
        System.IO.File.Exists(Path.Combine(ModsDir(instance), "otro.jar")).ShouldBeFalse();
    }

    [Fact]
    public async Task Project_details_are_read_with_body_gallery_and_links()
    {
        MapJson("/project/sodium", new
        {
            id = "AANobbMI", slug = "sodium", project_type = "mod", title = "Sodium", description = "Rápido", body = "# Hola\n<img src=\"https://x/y.png\">",
            loaders = new[] { "fabric" }, game_versions = new[] { "1.21.1" }, downloads = 10, followers = 2,
            published = "2020-01-01T00:00:00Z", updated = "2026-10-01T00:00:00Z",
            license = new { id = "LGPL-3.0-only", name = "GNU LGPL 3.0" },
            source_url = "https://github.com/x/y", donation_urls = new[] { new { platform = "ko-fi", url = "https://ko-fi.com/x" } },
            gallery = new[] { new { url = "https://cdn/a.png", featured = true, title = "Antes y después", ordering = 0 } },
        });

        var project = await new ModrinthClient(new Factory(_http), baseUrl: _server.BaseUrl).GetProjectAsync("sodium", TestContext.Current.CancellationToken);

        project.Body.ShouldStartWith("# Hola");
        project.License!.Name.ShouldBe("GNU LGPL 3.0");
        project.Gallery.ShouldHaveSingleItem().Title.ShouldBe("Antes y después");
        project.DonationUrls[0].Platform.ShouldBe("ko-fi");
    }

    // ── Modpacks ─────────────────────────────────────────────────────────

    /// <summary>Publica un .mrpack con el índice y los overrides indicados.</summary>
    private void PublishModpack(string project, object[] files, params (string Path, string Content)[] entries) =>
        PublishModpackFor("1.20.1", project, files, entries);

    private void PublishModpackFor(string minecraft, string project, object[] files, params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = new
            {
                formatVersion = 1,
                game = "minecraft",
                dependencies = new Dictionary<string, string> { ["minecraft"] = minecraft, ["fabric-loader"] = "0.15.11" },
                files,
            };
            using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
                writer.Write(JsonSerializer.Serialize(index));
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }

        MapJson(VersionsPath(project, null, null), new[] { Version("p1", project, File(project + ".mrpack", stream.ToArray())) });
    }

    private object PackFile(string path, byte[] content, string client = "required", string server = "required")
    {
        var served = Guid.NewGuid().ToString("N");
        _server.Map("/files/" + served, content);
        return new
        {
            path,
            hashes = new { sha1 = Convert.ToHexStringLower(SHA1.HashData(content)), sha512 = Convert.ToHexStringLower(SHA512.HashData(content)) },
            env = new { client, server },
            downloads = new[] { $"{_server.BaseUrl}/files/{served}" },
            fileSize = content.Length,
        };
    }

    [Fact]
    public async Task A_modpack_becomes_a_new_instance_with_its_loader_files_and_overrides()
    {
        PublishModpack("pack",
            [PackFile("mods/x.jar", "mod X"u8.ToArray()), PackFile("mods/solo-servidor.jar", "s"u8.ToArray(), client: "unsupported")],
            ("overrides/config/x.toml", "a=1"), ("overrides/options.txt", "comun"), ("client-overrides/options.txt", "cliente"));

        var instance = await CreateInstaller().InstallModpackAsync("pack", "Mi modpack", ct: TestContext.Current.CancellationToken);

        instance.Name.ShouldBe("Mi modpack");
        instance.MinecraftVersion.ShouldBe("1.20.1");
        instance.Loader.ShouldBe(ModLoader.Fabric);
        instance.LoaderChannel.ShouldBe(LoaderChannel.Specific);
        instance.LoaderVersion.ShouldBe("0.15.11");
        var dir = _store.GetDirectory(instance.Id);
        System.IO.File.ReadAllText(Path.Combine(dir, "mods", "x.jar")).ShouldBe("mod X");
        System.IO.File.Exists(Path.Combine(dir, "mods", "solo-servidor.jar")).ShouldBeFalse();
        System.IO.File.ReadAllText(Path.Combine(dir, "config", "x.toml")).ShouldBe("a=1");
        System.IO.File.ReadAllText(Path.Combine(dir, "options.txt")).ShouldBe("cliente"); // client-overrides tiene prioridad

        // Con el mismo nombre, la segunda instancia se numera.
        (await CreateInstaller().InstallModpackAsync("pack", "Mi modpack", ct: TestContext.Current.CancellationToken)).Name.ShouldBe("Mi modpack (2)");
    }

    [Theory]
    [InlineData("../fuera.txt")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("instance.json")]
    public async Task Modpacks_cannot_write_outside_the_instance_or_replace_its_files(string path)
    {
        PublishModpack("malo", [PackFile(path, "x"u8.ToArray())]);

        await Should.ThrowAsync<ModrinthException>(() => CreateInstaller().InstallModpackAsync("malo", "Malo", ct: TestContext.Current.CancellationToken));

        _store.LoadAll().ShouldBeEmpty(); // la instancia a medias se borra
        System.IO.File.Exists(_dir.Combine("fuera.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task Overrides_cannot_escape_the_instance_either()
    {
        PublishModpack("malo", [], ("overrides/../../fuera.txt", "x"));

        await Should.ThrowAsync<ModrinthException>(() => CreateInstaller().InstallModpackAsync("malo", "Malo", ct: TestContext.Current.CancellationToken));
        _store.LoadAll().ShouldBeEmpty();
    }

    [Fact]
    public void Only_https_or_local_downloads_are_allowed()
    {
        ContentInstaller.IsAllowedUrl("https://cdn.modrinth.com/data/x.jar", out _).ShouldBeTrue();
        ContentInstaller.IsAllowedUrl("http://127.0.0.1:5000/x.jar", out _).ShouldBeTrue();
        ContentInstaller.IsAllowedUrl("http://cdn.modrinth.com/data/x.jar", out _).ShouldBeFalse();
        ContentInstaller.IsAllowedUrl("file:///C:/Windows/win.ini", out _).ShouldBeFalse();
        ContentInstaller.IsSafeFileName("sodium-0.6.jar").ShouldBeTrue();
        ContentInstaller.IsSafeFileName("..").ShouldBeFalse();
        ContentInstaller.IsSafeFileName("a\\b.jar").ShouldBeFalse();
    }

    // ── Hosting: instalar en un servidor ─────────────────────────────────

    [Fact]
    public void Paper_servers_search_plugins()
    {
        var facets = new SearchQuery { Type = ContentType.Mod, Loaders = ["paper", "spigot", "bukkit"], ProjectTypeOverride = "plugin" }.BuildFacets();
        facets[0].ShouldBe(["project_type:plugin"]);
        facets.ShouldContain(f => f.SequenceEqual(new[] { "categories:paper", "categories:spigot", "categories:bukkit" }));
        new SearchQuery { Type = ContentType.Mod }.BuildFacets()[0].ShouldBe(["project_type:mod"]);
    }

    private static KonnorLauncher.Core.Hosting.HostedServer Server(KonnorLauncher.Core.Hosting.ServerSoftware software = KonnorLauncher.Core.Hosting.ServerSoftware.Fabric) =>
        new() { Id = "srv", Name = "Srv", Software = software, MinecraftVersion = "1.21.1", LoaderVersion = "0.16.9" };

    [Fact]
    public async Task Server_install_goes_to_its_folder_with_dependencies()
    {
        var serverDir = _dir.Combine("hosting", "srv");
        MapJson(VersionsPath("lithium", ["fabric"], "1.21.1"), new[]
        {
            Version("l1", "lithium", File("lithium.jar", "lithium"u8.ToArray()), deps: [new { project_id = "fabric-api", dependency_type = "required" }]),
        });
        MapJson(VersionsPath("fabric-api", ["fabric"], "1.21.1"), new[] { Version("f1", "fabric-api", File("fabric-api.jar", "api"u8.ToArray())) });

        var result = await CreateInstaller().InstallIntoServerAsync(Server(), serverDir, Path.Combine(serverDir, "cliente"), "lithium", ContentType.Mod,
            ct: TestContext.Current.CancellationToken);

        result.FileName.ShouldBe("lithium.jar");
        result.Dependencies.ShouldBe(["fabric-api.jar"]);
        System.IO.File.ReadAllText(Path.Combine(serverDir, "mods", "lithium.jar")).ShouldBe("lithium");
        System.IO.File.Exists(Path.Combine(serverDir, "mods", "fabric-api.jar")).ShouldBeTrue();
        ContentInstaller.InstalledProjects(serverDir).ShouldBe(["lithium", "fabric-api"], ignoreOrder: true);
    }

    [Fact]
    public async Task Server_install_sends_each_type_to_its_place()
    {
        var serverDir = _dir.Combine("hosting", "srv");
        var clientDir = Path.Combine(serverDir, "cliente");
        System.IO.Directory.CreateDirectory(serverDir);
        System.IO.File.WriteAllText(Path.Combine(serverDir, "server.properties"), "level-name=mundo\n");
        MapJson(VersionsPath("pack", ["minecraft"], "1.21.1"), new[] { Version("r1", "pack", File("pack.zip", "rp"u8.ToArray())) });
        MapJson(VersionsPath("dp", ["datapack"], "1.21.1"), new[] { Version("d1", "dp", File("dp.zip", "dp"u8.ToArray())) });
        MapJson(VersionsPath("shader", null, "1.21.1"), new[] { Version("s1", "shader", File("shader.zip", "sh"u8.ToArray())) });
        var installer = CreateInstaller();

        await installer.InstallIntoServerAsync(Server(), serverDir, clientDir, "pack", ContentType.ResourcePack, ct: TestContext.Current.CancellationToken);
        await installer.InstallIntoServerAsync(Server(), serverDir, clientDir, "dp", ContentType.DataPack, ct: TestContext.Current.CancellationToken);
        await installer.InstallIntoServerAsync(Server(), serverDir, clientDir, "shader", ContentType.Shader, ct: TestContext.Current.CancellationToken);

        System.IO.File.Exists(Path.Combine(clientDir, "resourcepacks", "pack.zip")).ShouldBeTrue(); // lo reciben los jugadores
        System.IO.File.Exists(Path.Combine(clientDir, "shaderpacks", "shader.zip")).ShouldBeTrue();
        System.IO.File.Exists(Path.Combine(serverDir, "mundo", "datapacks", "dp.zip")).ShouldBeTrue(); // en el mundo del servidor
    }

    [Fact]
    public async Task Server_install_rejects_another_loader_and_what_the_server_cannot_load()
    {
        var serverDir = _dir.Combine("hosting", "forge");
        MapJson("/version/v9", Version("v9", "sodium", File("sodium.jar", "x"u8.ToArray()))); // solo Fabric
        var forge = Server(KonnorLauncher.Core.Hosting.ServerSoftware.Forge);

        await Should.ThrowAsync<ModrinthException>(() => CreateInstaller().InstallIntoServerAsync(forge, serverDir, serverDir + "/cliente", "sodium",
            ContentType.Mod, ct: TestContext.Current.CancellationToken, versionId: "v9"));
        Directory.Exists(Path.Combine(serverDir, "mods")).ShouldBeFalse();

        var vanilla = Server(KonnorLauncher.Core.Hosting.ServerSoftware.Vanilla);
        ContentInstaller.ServerBlockReason(vanilla, ContentType.Mod).ShouldNotBeNull();
        ContentInstaller.ServerBlockReason(vanilla, ContentType.Modpack).ShouldNotBeNull();
        ContentInstaller.ServerBlockReason(Server(KonnorLauncher.Core.Hosting.ServerSoftware.Paper), ContentType.Modpack).ShouldNotBeNull();
        ContentInstaller.ServerBlockReason(vanilla, ContentType.DataPack).ShouldBeNull();
        ContentInstaller.ServerLoaders(Server(KonnorLauncher.Core.Hosting.ServerSoftware.Quilt), ContentType.Mod).ShouldBe(["quilt", "fabric"]);
        ContentInstaller.ServerLoaders(Server(KonnorLauncher.Core.Hosting.ServerSoftware.Paper), ContentType.Mod).ShouldBe(["paper", "spigot", "bukkit"]);
        await Should.ThrowAsync<ModrinthException>(() => CreateInstaller().InstallIntoServerAsync(vanilla, serverDir, serverDir + "/cliente", "sodium",
            ContentType.Mod, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Modpack_on_a_server_splits_server_and_client_files()
    {
        var serverDir = _dir.Combine("hosting", "srv");
        var clientDir = Path.Combine(serverDir, "cliente");
        PublishModpackFor("1.21.1", "pack",
            [
                PackFile("mods/comun.jar", "comun"u8.ToArray()),
                PackFile("mods/solo-cliente.jar", "cliente"u8.ToArray(), server: "unsupported"),
                PackFile("mods/solo-servidor.jar", "servidor"u8.ToArray(), client: "unsupported"),
            ],
            ("overrides/config/a.toml", "a=1"), ("server-overrides/config/a.toml", "a=servidor"), ("client-overrides/options.txt", "opciones"));

        var count = await CreateInstaller().InstallModpackIntoServerAsync(Server(), serverDir, clientDir, "pack", ct: TestContext.Current.CancellationToken);

        count.ShouldBe(3);
        System.IO.File.Exists(Path.Combine(serverDir, "mods", "comun.jar")).ShouldBeTrue();
        System.IO.File.Exists(Path.Combine(serverDir, "mods", "solo-servidor.jar")).ShouldBeTrue();
        System.IO.File.Exists(Path.Combine(serverDir, "mods", "solo-cliente.jar")).ShouldBeFalse();
        System.IO.File.Exists(Path.Combine(clientDir, "mods", "solo-cliente.jar")).ShouldBeTrue(); // lo reciben los jugadores
        System.IO.File.ReadAllText(Path.Combine(serverDir, "config", "a.toml")).ShouldBe("a=servidor"); // server-overrides tiene prioridad
        System.IO.File.ReadAllText(Path.Combine(clientDir, "options.txt")).ShouldBe("opciones");
    }

    [Fact]
    public async Task Modpack_for_another_version_or_touching_server_files_is_rejected()
    {
        var serverDir = _dir.Combine("hosting", "srv");
        PublishModpack("viejo", [PackFile("mods/x.jar", "x"u8.ToArray())]); // 1.21.1 en Modrinth, pero el índice dice 1.20.1
        await Should.ThrowAsync<ModrinthException>(() => CreateInstaller().InstallModpackIntoServerAsync(Server(), serverDir, serverDir + "/cliente", "viejo",
            ct: TestContext.Current.CancellationToken));

        PublishModpackFor("1.21.1", "malo", [PackFile("eula.txt", "eula=true"u8.ToArray())]);
        await Should.ThrowAsync<ModrinthException>(() => CreateInstaller().InstallModpackIntoServerAsync(Server(), serverDir, serverDir + "/cliente", "malo",
            ct: TestContext.Current.CancellationToken));
        System.IO.File.Exists(Path.Combine(serverDir, "eula.txt")).ShouldBeFalse();
    }
}
