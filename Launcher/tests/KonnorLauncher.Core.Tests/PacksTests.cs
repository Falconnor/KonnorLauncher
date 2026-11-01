using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Tests.Infrastructure;
using KonnorLauncher.Core.Verification;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Paquetes de recursos y de shaders: opcionales para el jugador o protegidos por el servidor.</summary>
public sealed class PacksTests : IAsyncLifetime
{
    private readonly LocalHttpServer _server = new();
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
        _dir.Dispose();
    }

    private sealed class Factory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private InstanceSync CreateSync() => new(new Factory(_http), metaDir: _dir.Combine("meta"));

    private string GameDir => _dir.Combine("instances", "srv");

    /// <summary>
    /// Servidor con una versión, un pack ZIP, un pack en carpeta y un shader. <paramref name="protect"/>
    /// null = el servidor no publica <c>protect_packs</c>.
    /// </summary>
    private void Publish(bool? protect)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["versions/1.21.1/1.21.1.json"] = Encoding.UTF8.GetBytes("""{ "id": "1.21.1", "mainClass": "net.minecraft.client.main.Main" }"""),
            ["resourcepacks/Servidor.zip"] = Encoding.UTF8.GetBytes("pack del servidor"),
            ["resourcepacks/Carpeta/pack.mcmeta"] = Encoding.UTF8.GetBytes("{\"pack\":{}}"),
            ["shaderpacks/Sombras.zip"] = Encoding.UTF8.GetBytes("shader del servidor"),
        };
        var manifest = new Dictionary<string, object>
        {
            ["delete_unauthorized"] = true,
            ["files"] = files.ToDictionary(f => f.Key, f => (object)new
            {
                sha256 = Convert.ToHexString(SHA256.HashData(f.Value)).ToLowerInvariant(),
                size = f.Value.Length,
            }),
        };
        if (protect is { } p)
            manifest["protect_packs"] = p;
        _server.Map("/client_manifest.json", JsonSerializer.Serialize(manifest));
        foreach (var (path, content) in files)
            _server.Map("/client/" + path, content);
    }

    private void WriteLocal(string relative, string content)
    {
        var path = Path.Combine(GameDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadLocal(string relative) => File.ReadAllText(Path.Combine(GameDir, relative));

    private bool Exists(string relative) => File.Exists(Path.Combine(GameDir, relative));

    private GameInstance NewInstance(bool installPacks) =>
        new() { Id = "srv", Name = "Servidor", SyncWithServer = true, ServerUrl = _server.BaseUrl, InstallPacks = installPacks };

    private async Task SyncAsync(GameInstance instance)
    {
        var result = await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);
        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_the_switch_packs_are_not_installed_and_the_players_are_kept()
    {
        Publish(protect: false);
        WriteLocal("resourcepacks/Mio.zip", "pack del jugador");

        await SyncAsync(NewInstance(installPacks: false));

        Exists("resourcepacks/Servidor.zip").ShouldBeFalse();
        Exists("shaderpacks/Sombras.zip").ShouldBeFalse();
        ReadLocal("resourcepacks/Mio.zip").ShouldBe("pack del jugador"); // la limpieza no toca los packs
    }

    [Fact]
    public async Task With_the_switch_only_missing_packs_are_installed()
    {
        Publish(protect: false);
        WriteLocal("resourcepacks/Servidor.zip", "mi versión");          // mismo nombre que uno del servidor
        WriteLocal("resourcepacks/Carpeta/otro.txt", "pack del jugador"); // carpeta con el mismo nombre
        WriteLocal("shaderpacks/Mio.zip", "shader del jugador");

        var instance = NewInstance(installPacks: true);
        await SyncAsync(instance);

        ReadLocal("resourcepacks/Servidor.zip").ShouldBe("mi versión");  // no se reemplaza
        Exists("resourcepacks/Carpeta/pack.mcmeta").ShouldBeFalse();     // el del jugador se respeta entero
        ReadLocal("shaderpacks/Sombras.zip").ShouldBe("shader del servidor");
        ReadLocal("shaderpacks/Mio.zip").ShouldBe("shader del jugador");  // nunca se borran los suyos
        instance.IsPacksLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task Protected_packs_are_forced_repaired_and_cleaned_like_the_rest()
    {
        Publish(protect: true);
        WriteLocal("resourcepacks/Servidor.zip", "mi versión");
        WriteLocal("resourcepacks/Mio.zip", "pack del jugador");

        var instance = NewInstance(installPacks: false);
        await SyncAsync(instance);

        instance.InstallPacks.ShouldBeTrue();  // el servidor marca la casilla...
        instance.IsPacksLocked.ShouldBeTrue(); // ...y la bloquea
        ReadLocal("resourcepacks/Servidor.zip").ShouldBe("pack del servidor");
        ReadLocal("resourcepacks/Carpeta/pack.mcmeta").ShouldBe("{\"pack\":{}}");
        Exists("resourcepacks/Mio.zip").ShouldBeFalse(); // delete_unauthorized también en los packs
    }

    [Fact]
    public async Task The_lock_follows_the_server_and_the_switch_stays_on()
    {
        var instance = NewInstance(installPacks: false);
        Publish(protect: true);
        await SyncAsync(instance);
        instance.IsPacksLocked.ShouldBeTrue();

        Publish(protect: false);
        (await CreateSync().RefreshPolicyAsync(instance, TestContext.Current.CancellationToken)).ShouldBeTrue();
        instance.IsPacksLocked.ShouldBeFalse();
        instance.InstallPacks.ShouldBeTrue(); // el jugador puede desmarcarla ahora
    }

    [Fact]
    public async Task Older_servers_do_not_protect_packs()
    {
        Publish(protect: null);
        var instance = NewInstance(installPacks: false);
        await SyncAsync(instance);

        instance.IsPacksLocked.ShouldBeFalse();
        Exists("resourcepacks/Servidor.zip").ShouldBeFalse();
    }

    [Fact]
    public void Pack_paths_are_recognised_by_their_first_folder()
    {
        ClientVerifier.PackOf("resourcepacks/A.zip").ShouldBe(("resourcepacks", "A.zip"));
        ClientVerifier.PackOf("shaderpacks/B/shaders/x.fsh").ShouldBe(("shaderpacks", "B"));
        ClientVerifier.PackOf("mods/resourcepacks.jar").ShouldBeNull();
        ClientVerifier.PackOf("resourcepacks").ShouldBeNull();
        ManifestParser.Parse("""{ "files": {}, "protect_packs": true }""").ProtectPacks.ShouldBe(true);
        ManifestParser.Parse("""{ "files": {} }""").ProtectPacks.ShouldBeNull();
    }
}
