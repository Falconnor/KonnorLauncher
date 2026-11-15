using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Modrinth;
using KonnorLauncher.Core.Stores;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Tiendas de Explorar: CurseForge, FTB y la elección entre ellas (con respuestas simuladas).</summary>
public sealed class StoresTests : IDisposable
{
    private const string Cf = "https://cf.test/v1";
    private const string Ftb = "https://ftb.test/public";

    private readonly TempDir _dir = new();
    private readonly FakeHandler _handler = new();
    private readonly InstanceStore _store;

    public StoresTests() => _store = new InstanceStore(_dir.Combine("instances"));

    public void Dispose() => _dir.Dispose();

    /// <summary>Respuestas por método y ruta (sin la consulta); guarda las peticiones.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = [];
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        public void Json(string method, string url, object value) =>
            _routes[$"{method} {new Uri(url).GetLeftPart(UriPartial.Path)}"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
            };

        public void Bytes(string url, byte[] value) =>
            _routes[$"GET {new Uri(url).GetLeftPart(UriPartial.Path)}"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return _routes.TryGetValue($"{request.Method} {request.RequestUri!.GetLeftPart(UriPartial.Path)}", out var route)
                ? route(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private CurseForgeClient CurseForge(string? key = "clave") => new(new Factory(_handler), () => key, baseUrl: Cf);
    private FtbClient FtbClient() => new(new Factory(_handler), baseUrl: Ftb);

    private ContentStores Stores(StoreKind current = StoreKind.Modrinth) =>
        new(new ModrinthClient(new Factory(_handler), baseUrl: "https://modrinth.test/v2"), CurseForge(), FtbClient(), current);

    private ContentInstaller Installer(ContentStores stores) => new(stores.Modrinth, _store, stores: stores);

    private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    private static object CfFile(int id, int modId, string name, string? url, byte[]? content = null, string[]? versions = null, object[]? deps = null) => new
    {
        id,
        modId,
        displayName = name,
        fileName = name,
        releaseType = 1,
        fileDate = "2026-05-01T10:00:00Z",
        fileLength = content?.Length ?? 10,
        downloadUrl = url,
        gameVersions = versions ?? ["1.20.1", "Fabric", "Client"],
        hashes = new[] { new { value = Sha1(content ?? [1]), algo = 1 }, new { value = "md5", algo = 2 } },
        dependencies = deps ?? [],
    };

    // ── CurseForge ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CurseForge_search_sends_the_key_and_filters_and_maps_the_results()
    {
        _handler.Json("GET", $"{Cf}/mods/search", new
        {
            data = new[]
            {
                new
                {
                    id = 238222, name = "JEI", slug = "jei", summary = "Recetas", downloadCount = 1.5e8, thumbsUpCount = 10, classId = 6,
                    logo = new { thumbnailUrl = "https://media.test/jei.png" },
                    authors = new[] { new { name = "mezz" } },
                    categories = new[] { new { id = 1, name = "Utilidades", classId = 6 } },
                    latestFilesIndexes = new[] { new { gameVersion = "1.20.1", fileId = 5, modLoader = 1 } },
                    dateModified = "2026-05-01T10:00:00Z",
                },
            },
            pagination = new { index = 0, pageSize = 20, resultCount = 1, totalCount = 25_000 },
        });

        var page = await CurseForge().SearchAsync(new SearchQuery
        {
            Type = ContentType.Mod, Text = "jei", Sort = SearchSort.Downloads, GameVersions = ["1.20.1"], Loaders = ["forge"],
        }, TestContext.Current.CancellationToken);

        var (request, _) = _handler.Requests.Single();
        request.Headers.GetValues("x-api-key").ShouldBe(["clave"]);
        var query = request.RequestUri!.Query;
        query.ShouldContain("gameId=432");
        query.ShouldContain("classId=6");
        query.ShouldContain("sortField=6");
        query.ShouldContain("searchFilter=jei");
        query.ShouldContain("gameVersion=1.20.1");
        query.ShouldContain("modLoaderType=1");

        page.TotalHits.ShouldBe(10_000); // la API no da más
        var hit = page.Hits.Single();
        hit.ProjectId.ShouldBe("cf:238222");
        hit.ProjectType.ShouldBe("mod");
        hit.Author.ShouldBe("mezz");
        hit.Downloads.ShouldBe(150_000_000);
        hit.IconUrl.ShouldBe("https://media.test/jei.png");
        hit.Categories.ShouldBe(["Utilidades", "forge"]);
        hit.DisplayCategories.ShouldBe(["Utilidades"]);
    }

    [Fact]
    public async Task CurseForge_without_a_key_asks_for_it()
    {
        var error = await Should.ThrowAsync<ModrinthException>(() => CurseForge(key: " ").SearchAsync(new SearchQuery(), TestContext.Current.CancellationToken));
        error.Message.ShouldContain("clave de la API de CurseForge");
        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CurseForge_versions_keep_loaders_hashes_dependencies_and_files_without_download()
    {
        _handler.Json("GET", $"{Cf}/mods/10/files", new
        {
            data = new[]
            {
                CfFile(2, 10, "nuevo.jar", null, versions: ["1.20.1", "Forge"]),
                CfFile(1, 10, "viejo.jar", "https://edge.test/viejo.jar", versions: ["1.20.1", "Fabric", "Quilt"], deps: [new { modId = 99, relationType = 3 }, new { modId = 98, relationType = 2 }]),
            },
            pagination = new { totalCount = 2 },
        });

        var versions = await CurseForge().GetVersionsAsync("cf:10", ["fabric"], ["1.20.1"], TestContext.Current.CancellationToken);

        var version = versions.Single(); // el de Forge no es compatible
        version.Id.ShouldBe("cf:10:1");
        version.ProjectId.ShouldBe("cf:10");
        version.Loaders.ShouldBe(["fabric", "quilt"]);
        version.GameVersions.ShouldBe(["1.20.1"]);
        version.PrimaryFile!.Url.ShouldBe("https://edge.test/viejo.jar");
        version.PrimaryFile.Hashes.Sha1.ShouldBe(Sha1([1]));
        version.Dependencies.Single().ProjectId.ShouldBe("cf:99"); // solo las obligatorias

        var all = await CurseForge().GetVersionsAsync("cf:10", ct: TestContext.Current.CancellationToken);
        all.Single(v => v.Id == "cf:10:2").PrimaryFile!.Url.ShouldBeEmpty(); // su autor no permite descargarlo fuera de CurseForge
    }

    [Fact]
    public async Task A_CurseForge_file_without_download_is_not_installed()
    {
        _handler.Json("GET", $"{Cf}/mods/10/files/2", new { data = CfFile(2, 10, "privado.jar", null) });
        var instance = _store.Create(new GameInstance { Name = "Fabric", MinecraftVersion = "1.20.1", Loader = ModLoader.Fabric });

        var error = await Should.ThrowAsync<ModrinthException>(() =>
            Installer(Stores()).InstallAsync(instance, "cf:10", ContentType.Mod, versionId: "cf:10:2", ct: TestContext.Current.CancellationToken));
        error.Message.ShouldContain("no permite descargarlo fuera de CurseForge");
    }

    [Fact]
    public async Task A_CurseForge_modpack_installs_its_files_in_their_folders_and_reports_the_ones_it_cannot_download()
    {
        var mod = "mod"u8.ToArray();
        var pack = "pack"u8.ToArray();
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                await writer.WriteAsync(JsonSerializer.Serialize(new
                {
                    minecraft = new { version = "1.20.1", modLoaders = new[] { new { id = "forge-47.2.0", primary = true } } },
                    overrides = "overrides",
                    files = new object[]
                    {
                        new { projectID = 1, fileID = 11, required = true },
                        new { projectID = 2, fileID = 22, required = true },
                        new { projectID = 3, fileID = 33, required = true },
                        new { projectID = 4, fileID = 44, required = false },
                    },
                }));
            await using (var writer = new StreamWriter(archive.CreateEntry("overrides/config/a.toml").Open()))
                await writer.WriteAsync("a=1");
        }

        _handler.Json("GET", $"{Cf}/mods/500/files", new { data = new[] { CfFile(900, 500, "pack.zip", "https://edge.test/pack.zip", zip.ToArray(), ["1.20.1", "Forge"]) } });
        _handler.Bytes("https://edge.test/pack.zip", zip.ToArray());
        _handler.Json("POST", $"{Cf}/mods/files", new
        {
            data = new[] { CfFile(11, 1, "a.jar", "https://edge.test/a.jar", mod), CfFile(22, 2, "rp.zip", "https://edge.test/rp.zip", pack), CfFile(33, 3, "privado.jar", null) },
        });
        _handler.Json("POST", $"{Cf}/mods", new
        {
            data = new[] { new { id = 1, name = "Mod A", classId = 6 }, new { id = 2, name = "Pack", classId = 12 }, new { id = 3, name = "Privado", classId = 6 } },
        });
        _handler.Bytes("https://edge.test/a.jar", mod);
        _handler.Bytes("https://edge.test/rp.zip", pack);

        var skipped = new List<string>();
        var instance = await Installer(Stores(StoreKind.CurseForge)).InstallModpackAsync("cf:500", "Mi pack", skipped: skipped, ct: TestContext.Current.CancellationToken);

        instance.MinecraftVersion.ShouldBe("1.20.1");
        instance.Loader.ShouldBe(ModLoader.Forge);
        instance.LoaderVersion.ShouldBe("47.2.0");
        var dir = _store.GetDirectory(instance.Id);
        File.ReadAllText(Path.Combine(dir, "mods", "a.jar")).ShouldBe("mod");
        File.ReadAllText(Path.Combine(dir, "resourcepacks", "rp.zip")).ShouldBe("pack");
        File.ReadAllText(Path.Combine(dir, "config", "a.toml")).ShouldBe("a=1");
        skipped.ShouldBe(["Privado"]);
        // El opcional no se pide.
        _handler.Requests.Single(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/mods/files")).Body!.ShouldNotContain("44");
    }

    [Theory]
    [InlineData("forge-47.2.0", ModLoader.Forge, "47.2.0")]
    [InlineData("neoforge-21.1.65", ModLoader.NeoForge, "21.1.65")]
    [InlineData("fabric-0.15.11", ModLoader.Fabric, "0.15.11")]
    [InlineData("quilt-0.26.0", ModLoader.Quilt, "0.26.0")]
    public void CurseForge_manifest_loaders_are_recognized(string id, ModLoader loader, string version) =>
        CurseForgeManifest.ParseLoader(id).ShouldBe((loader, version));

    [Fact]
    public void An_unknown_CurseForge_loader_is_rejected() =>
        Should.Throw<ModrinthException>(() => CurseForgeManifest.ParseLoader("rift-1.0"));

    // ── FTB ─────────────────────────────────────────────────────────────────

    private static object FtbPack(int id, string name, long installs, string minecraft = "1.21.1", string loader = "neoforge") => new
    {
        status = "success",
        id,
        name,
        slug = name.ToLowerInvariant().Replace(' ', '-'),
        synopsis = "Resumen de " + name,
        description = "# " + name,
        released = 1_700_000_000L + id,
        updated = 1_700_000_000L + id,
        installs,
        plays = 5,
        art = new[] { new { url = $"https://cdn.test/{id}.webp", type = "square", order = 0 }, new { url = $"https://cdn.test/{id}-s.webp", type = "screenshot", order = 1 } },
        tags = new[] { new { name = "1.21.1" }, new { name = "Skyblock" } },
        authors = new[] { new { name = "FTB Team" } },
        links = new[] { new { name = "Wiki", link = "https://docs.test" } },
        versions = new[]
        {
            new
            {
                id = id * 10, name = "1.0.0", type = "Release", released = 1_700_000_000L, updated = 0L,
                targets = new[] { new { name = "minecraft", version = minecraft, type = "game" }, new { name = loader, version = "21.1.1", type = "modloader" } },
            },
        },
    };

    [Fact]
    public async Task FTB_lists_popular_modpacks_page_by_page()
    {
        _handler.Json("GET", $"{Ftb}/modpack/popular/installs/100", new { status = "success", packs = new[] { 1, 2, 3 } });
        _handler.Json("GET", $"{Ftb}/modpack/1", FtbPack(1, "Uno", 300));
        _handler.Json("GET", $"{Ftb}/modpack/2", FtbPack(2, "Dos", 200));
        _handler.Json("GET", $"{Ftb}/modpack/3", FtbPack(3, "Tres", 100, loader: "fabric"));
        var ftb = FtbClient();

        var first = await ftb.SearchAsync(new SearchQuery { Type = ContentType.Modpack, Limit = 2 }, TestContext.Current.CancellationToken);
        first.TotalHits.ShouldBe(3);
        first.Hits.Select(h => h.ProjectId).ShouldBe(["ftb:1", "ftb:2"]);
        first.Hits[0].IconUrl.ShouldBe("https://cdn.test/1.webp");
        first.Hits[0].DisplayCategories.ShouldBe(["Skyblock"]); // la versión de Minecraft no es una categoría
        _handler.Requests.ShouldNotContain(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/modpack/3")); // solo los de la página

        var fabric = await ftb.SearchAsync(new SearchQuery { Type = ContentType.Modpack, Loaders = ["fabric"] }, TestContext.Current.CancellationToken);
        fabric.Hits.Single().Title.ShouldBe("Tres");

        (await ftb.SearchAsync(new SearchQuery { Type = ContentType.Mod }, TestContext.Current.CancellationToken)).Hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_FTB_modpack_installs_its_files_with_their_paths()
    {
        var mod = "mod"u8.ToArray();
        var config = "a=1"u8.ToArray();
        _handler.Json("GET", $"{Ftb}/modpack/7", FtbPack(7, "Siete", 10));
        _handler.Json("GET", $"{Ftb}/modpack/7/70", new
        {
            status = "success",
            targets = new[] { new { name = "minecraft", version = "1.21.1", type = "game" }, new { name = "neoforge", version = "21.1.1", type = "modloader" } },
            files = new object[]
            {
                new { path = "./mods/", name = "a.jar", url = "https://files.test/a.jar", sha1 = Sha1(mod), size = mod.Length, serveronly = false },
                new { path = "./config", name = "a.toml", url = "https://files.test/a.toml", hashes = new { sha1 = Sha1(config) }, size = config.Length, serveronly = false },
                new { path = "./mods/", name = "servidor.jar", url = "https://files.test/s.jar", sha1 = Sha1(mod), size = 3, serveronly = true },
            },
        });
        _handler.Bytes("https://files.test/a.jar", mod);
        _handler.Bytes("https://files.test/a.toml", config);

        var instance = await Installer(Stores(StoreKind.Ftb)).InstallModpackAsync("ftb:7", "Siete", ct: TestContext.Current.CancellationToken);

        instance.Loader.ShouldBe(ModLoader.NeoForge);
        instance.LoaderVersion.ShouldBe("21.1.1");
        var dir = _store.GetDirectory(instance.Id);
        File.ReadAllText(Path.Combine(dir, "mods", "a.jar")).ShouldBe("mod");
        File.ReadAllText(Path.Combine(dir, "config", "a.toml")).ShouldBe("a=1");
        File.Exists(Path.Combine(dir, "mods", "servidor.jar")).ShouldBeFalse();
    }

    [Fact]
    public async Task FTB_project_page_has_its_versions_gallery_and_link()
    {
        _handler.Json("GET", $"{Ftb}/modpack/7", FtbPack(7, "Siete", 10));
        var ftb = FtbClient();

        var details = await ftb.GetProjectAsync("ftb:7", TestContext.Current.CancellationToken);
        details.Body.ShouldBe("# Siete");
        details.Gallery.Single().Url.ShouldBe("https://cdn.test/7-s.webp");
        details.WebUrl.ShouldBe("https://www.feed-the-beast.com/modpacks/7-siete");
        details.Loaders.ShouldBe(["neoforge"]);

        var version = (await ftb.GetVersionsAsync("ftb:7", ct: TestContext.Current.CancellationToken)).Single();
        version.Id.ShouldBe("ftb:7:70");
        version.VersionType.ShouldBe("release");
        (await ftb.GetVersionAsync("ftb:7:70", TestContext.Current.CancellationToken)).Name.ShouldBe("1.0.0");
    }

    // ── Elección de tienda ──────────────────────────────────────────────────

    [Fact]
    public async Task Projects_go_to_the_store_of_their_id_whatever_store_is_chosen()
    {
        _handler.Json("GET", $"{Ftb}/modpack/7", FtbPack(7, "Siete", 10));
        var stores = Stores(StoreKind.CurseForge);

        (await stores.GetProjectAsync("ftb:7", TestContext.Current.CancellationToken)).Title.ShouldBe("Siete");
        stores.Current.Kind.ShouldBe(StoreKind.CurseForge);

        var changed = 0;
        stores.Changed += (_, _) => changed++;
        stores.Select(StoreKind.CurseForge);
        stores.Select(StoreKind.Ftb);
        changed.ShouldBe(1);
        stores.Supports(ContentType.Mod).ShouldBeFalse();
    }

    [Theory]
    [InlineData("cf:1", StoreKind.CurseForge)]
    [InlineData("ftb:1:2", StoreKind.Ftb)]
    [InlineData("AANobbMI", StoreKind.Modrinth)]
    public void The_store_comes_from_the_id(string id, StoreKind kind) => ContentStores.KindOf(id).ShouldBe(kind);

    [Theory]
    [InlineData("curseforge", StoreKind.CurseForge)]
    [InlineData(" FTB ", StoreKind.Ftb)]
    [InlineData("", StoreKind.Modrinth)]
    [InlineData("otra", StoreKind.Modrinth)]
    public void The_saved_store_is_read_back(string value, StoreKind kind)
    {
        ContentStores.Parse(value).ShouldBe(kind);
        ContentStores.Parse(ContentStores.Format(kind)).ShouldBe(kind);
    }
}
