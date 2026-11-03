using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Modrinth;
using KonnorLauncher.Core.Tests.Infrastructure;
using KonnorLauncher.Core.Verification;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>
/// Manifest con la versión del juego (<c>minecraft</c>): el launcher instala el juego y descarga de
/// Modrinth los archivos que llevan SHA-512; el resto, del servidor.
/// </summary>
public sealed class ServerGameTests : IAsyncLifetime
{
    private readonly LocalHttpServer _server = new();
    private readonly LocalHttpServer _mirror = new();
    private readonly TempDir _dir = new();
    private HttpClient _http = null!;

    public ValueTask InitializeAsync()
    {
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        await _mirror.DisposeAsync();
        _dir.Dispose();
    }

    private static readonly byte[] EnModrinth = Encoding.UTF8.GetBytes("mod que está en Modrinth");
    private static readonly byte[] SoloServidor = Encoding.UTF8.GetBytes("mod que solo tiene el servidor");
    private static readonly byte[] Config = Encoding.UTF8.GetBytes("opcion=1");

    private string GameDir => _dir.Combine("game");

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string Sha512(byte[] data) => Convert.ToHexString(SHA512.HashData(data)).ToLowerInvariant();

    /// <summary>Servidor con Fabric en el manifest, dos mods con SHA-512 y un archivo de configuración.</summary>
    private void Publish(bool serveModrinthModFromServer = false)
    {
        var files = new Dictionary<string, (byte[] Data, bool Modrinth)>
        {
            ["mods/en-modrinth.jar"] = (EnModrinth, true),
            ["mods/solo-servidor.jar"] = (SoloServidor, true),
            ["config/mod.properties"] = (Config, false),
        };
        var manifest = new Dictionary<string, object>
        {
            ["delete_unauthorized"] = true,
            ["minecraft"] = new { version = "1.21.1", loader = "fabric", loader_version = "0.19.3" },
            ["files"] = files.ToDictionary(f => f.Key, f => f.Value.Modrinth
                ? (object)new { sha256 = Sha256(f.Value.Data), sha512 = Sha512(f.Value.Data), size = f.Value.Data.Length }
                : new { sha256 = Sha256(f.Value.Data), size = f.Value.Data.Length }),
        };
        _server.Map("/client_manifest.json", JsonSerializer.Serialize(manifest));
        foreach (var (path, (data, _)) in files)
        {
            if (path != "mods/en-modrinth.jar" || serveModrinthModFromServer)
                _server.Map("/client/" + path, data);
        }
    }

    private ClientVerifier CreateVerifier(Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>>? lookup) =>
        new(_http) { MirrorLookup = lookup, RetryDelay = TimeSpan.FromMilliseconds(10) };

    private Task<VerificationResult> VerifyAsync(ClientVerifier verifier) =>
        verifier.VerifyAsync(new VerifierOptions { GameDir = GameDir, ServerUrl = _server.BaseUrl, EnableVerify = true, DeleteUnauthorized = true },
            null, TestContext.Current.CancellationToken);

    /// <summary>Modrinth "conoce" solo el primer mod.</summary>
    private Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> MirrorKnowing(string path) =>
        (hashes, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(
            hashes.Where(h => h == Sha512(EnModrinth)).ToDictionary(h => h, _ => _mirror.BaseUrl + path));

    private void WriteLocal(string relative, string content)
    {
        var path = Path.Combine(GameDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private byte[] ReadLocal(string relative) => File.ReadAllBytes(Path.Combine(GameDir, relative));

    [Fact]
    public void Parser_reads_the_game_and_the_sha512()
    {
        var hash = new string('a', 128);
        var manifest = ManifestParser.Parse($$"""
            { "minecraft": { "version": "1.20.1", "loader": "forge", "loader_version": "47.3.0" },
              "files": { "mods/a.jar": { "sha256": "{{new string('0', 64)}}", "sha512": "{{hash.ToUpperInvariant()}}", "size": 1 },
                         "config/b.txt": { "sha256": "{{new string('0', 64)}}", "sha512": "corto", "size": 1 } } }
            """);

        manifest.Game.ShouldBe(new ManifestGame("1.20.1", ModLoader.Forge, "47.3.0"));
        manifest.Files.Single(f => f.UrlPath == "mods/a.jar").Sha512.ShouldBe(hash);
        manifest.Files.Single(f => f.UrlPath == "config/b.txt").Sha512.ShouldBeNull(); // no válido: se ignora
        ManifestParser.Parse("""{ "files": {} }""").Game.ShouldBeNull();
        ManifestParser.Parse("""{ "minecraft": { "version": "1.21.1", "loader": "vanilla" }, "files": {} }""").Game
            .ShouldBe(new ManifestGame("1.21.1", ModLoader.Vanilla, null));
    }

    [Theory]
    [InlineData("""{ "version": "1.21.1", "loader": "optifine", "loader_version": "1" }""")]
    [InlineData("""{ "version": "1.21.1", "loader": "fabric" }""")]
    [InlineData("""{ "version": "../1.21.1", "loader": "vanilla" }""")]
    [InlineData("\"1.21.1\"")]
    public void Parser_rejects_an_invalid_game(string game) =>
        Should.Throw<VerificationException>(() => ManifestParser.Parse($$"""{ "minecraft": {{game}}, "files": {} }"""));

    [Fact]
    public async Task Mods_on_Modrinth_come_from_there_and_the_rest_from_the_server()
    {
        Publish();
        _mirror.Map("/cdn/en-modrinth.jar", EnModrinth);
        WriteLocal("mods/sobra.jar", "no está en el manifest");
        WriteLocal("versions/1.21.1/1.21.1.json", "{}"); // las instala el launcher: no se limpian
        WriteLocal("libraries/x/x.jar", "libreria");

        var result = await VerifyAsync(CreateVerifier(MirrorKnowing("/cdn/en-modrinth.jar")));

        result.Success.ShouldBeTrue();
        ReadLocal("mods/en-modrinth.jar").ShouldBe(EnModrinth);
        ReadLocal("mods/solo-servidor.jar").ShouldBe(SoloServidor);
        ReadLocal("config/mod.properties").ShouldBe(Config);
        _server.Requests.ShouldNotContain("/client/mods/en-modrinth.jar");
        _mirror.Requests.ShouldContain("/cdn/en-modrinth.jar");
        File.Exists(Path.Combine(GameDir, "mods", "sobra.jar")).ShouldBeFalse();
        File.Exists(Path.Combine(GameDir, "versions", "1.21.1", "1.21.1.json")).ShouldBeTrue();
        File.Exists(Path.Combine(GameDir, "libraries", "x", "x.jar")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_wrong_or_missing_file_on_Modrinth_falls_back_to_the_server()
    {
        Publish(serveModrinthModFromServer: true);
        _mirror.Map("/cdn/en-modrinth.jar", Encoding.UTF8.GetBytes("contenido distinto, mismo nombre"));

        (await VerifyAsync(CreateVerifier(MirrorKnowing("/cdn/en-modrinth.jar")))).Success.ShouldBeTrue();
        ReadLocal("mods/en-modrinth.jar").ShouldBe(EnModrinth); // se comprueba con el SHA-256 del manifest

        File.Delete(Path.Combine(GameDir, "mods", "en-modrinth.jar"));
        (await VerifyAsync(CreateVerifier(MirrorKnowing("/cdn/no-existe.jar")))).Success.ShouldBeTrue();
        ReadLocal("mods/en-modrinth.jar").ShouldBe(EnModrinth);
    }

    [Fact]
    public async Task If_Modrinth_does_not_answer_everything_comes_from_the_server()
    {
        Publish(serveModrinthModFromServer: true);

        var result = await VerifyAsync(CreateVerifier((_, _) => throw new ModrinthException("sin conexión")));

        result.Success.ShouldBeTrue();
        ReadLocal("mods/en-modrinth.jar").ShouldBe(EnModrinth);
    }

    [Fact]
    public async Task Files_already_in_place_do_not_ask_Modrinth()
    {
        Publish();
        WriteLocal("mods/en-modrinth.jar", Encoding.UTF8.GetString(EnModrinth));
        WriteLocal("mods/solo-servidor.jar", Encoding.UTF8.GetString(SoloServidor));
        WriteLocal("config/mod.properties", Encoding.UTF8.GetString(Config));
        var asked = false;

        (await VerifyAsync(CreateVerifier((h, ct) =>
        {
            asked = true;
            return MirrorKnowing("/cdn/en-modrinth.jar")(h, ct);
        }))).Success.ShouldBeTrue();

        asked.ShouldBeFalse(); // solo descargas y reparaciones
    }

    [Fact]
    public async Task Sync_adopts_the_version_and_loader_of_the_manifest()
    {
        Publish(serveModrinthModFromServer: true);
        var instance = new GameInstance
        {
            Id = "srv", Name = "Servidor", SyncWithServer = true, ServerUrl = _server.BaseUrl,
            MinecraftVersion = "fabric-loader-0.16.0-1.21.1", InstalledVersionId = "fabric-loader-0.16.0-1.21.1",
        };
        var sync = new InstanceSync(new Factory(_http), metaDir: _dir.Combine("meta"));

        var result = await sync.SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Game.ShouldBe(new ManifestGame("1.21.1", ModLoader.Fabric, "0.19.3"));
        InstanceSync.AdoptServerVersion(instance, result).ShouldBeTrue();
        instance.MinecraftVersion.ShouldBe("1.21.1");
        instance.Loader.ShouldBe(ModLoader.Fabric);
        instance.LoaderVersion.ShouldBe("0.19.3");
        instance.LoaderChannel.ShouldBe(LoaderChannel.Specific);
        instance.InstalledVersionId.ShouldBeNull();
        instance.Summary.ShouldBe("1.21.1 · Fabric 0.19.3");
        InstanceSync.AdoptServerVersion(instance, result).ShouldBeFalse(); // ya la tiene
    }

    [Fact]
    public async Task Modrinth_lookup_returns_the_file_with_the_requested_hash()
    {
        var wanted = Sha512(EnModrinth);
        var handler = new StubHandler(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [wanted] = new
            {
                id = "v1",
                files = new object[]
                {
                    new { url = "https://cdn.modrinth.com/otro.jar", filename = "otro.jar", hashes = new { sha512 = new string('b', 128) } },
                    new { url = "https://cdn.modrinth.com/bueno.jar", filename = "bueno.jar", hashes = new { sha512 = wanted } },
                },
            },
        }));
        using var http = new HttpClient(handler);
        var client = new ModrinthClient(new Factory(http), baseUrl: "https://api.example.test/v2");

        var found = await client.FindFilesBySha512Async([wanted.ToUpperInvariant(), Sha512(SoloServidor)], TestContext.Current.CancellationToken);

        found.ShouldHaveSingleItem().Value.ShouldBe("https://cdn.modrinth.com/bueno.jar");
        handler.Request.ShouldNotBeNull();
        handler.Request!.Method.ShouldBe(HttpMethod.Post);
        handler.Request.RequestUri!.AbsoluteUri.ShouldBe("https://api.example.test/v2/version_files");
        handler.Body.ShouldContain("\"algorithm\":\"sha512\"");
        handler.Body.ShouldContain(wanted); // en minúsculas, como los guarda Modrinth
    }

    private sealed class Factory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
