using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Consulta de los servidores al abrir el launcher y aviso de actualización disponible.</summary>
public sealed class UpdateCheckTests : IAsyncLifetime
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

    private static readonly byte[] VersionJson = Encoding.UTF8.GetBytes("""{ "id": "1.21.1", "mainClass": "net.minecraft.client.main.Main" }""");

    /// <summary>Manifest con la versión indicada. <paramref name="serveFiles"/> false = los archivos dan 404.</summary>
    private void Publish(string version, bool serveFiles = true, bool protectPacks = false)
    {
        var manifest = new Dictionary<string, object>
        {
            ["client_version"] = version,
            ["protect_packs"] = protectPacks,
            ["files"] = new Dictionary<string, object>
            {
                ["versions/1.21.1/1.21.1.json"] = new { sha256 = Convert.ToHexString(SHA256.HashData(VersionJson)).ToLowerInvariant(), size = VersionJson.Length },
            },
        };
        _server.Map("/client_manifest.json", JsonSerializer.Serialize(manifest));
        _server.Map("/client/versions/1.21.1/1.21.1.json", serveFiles ? VersionJson : Encoding.UTF8.GetBytes("no es el archivo"));
    }

    [Theory]
    [InlineData("1.0.10", "1.0.9", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("2.0", "10.0", -1)]
    [InlineData("1.1-beta", "1.1-Beta", 0)]
    [InlineData("1.2", "1.1.9", 1)]
    public void Client_versions_compare_by_parts(string a, string b, int expected) =>
        ClientVersions.Compare(a, b).ShouldBe(expected);

    [Fact]
    public void Only_a_later_version_is_an_update()
    {
        ClientVersions.IsNewer("1.0.1", "1.0.0").ShouldBeTrue();
        ClientVersions.IsNewer("1.0.0", "1.0.0").ShouldBeFalse();
        ClientVersions.IsNewer("0.9", "1.0").ShouldBeFalse(); // el servidor volvió atrás: no es una actualización
        ClientVersions.IsNewer("1.0", null).ShouldBeTrue();
        ClientVersions.IsNewer(null, "1.0").ShouldBeFalse();
    }

    [Fact]
    public async Task Opening_the_launcher_detects_updates_without_downloading_anything()
    {
        var store = new InstanceStore(_dir.Combine("instances"));
        var instance = store.Create(new GameInstance { Name = "Servidor", SyncWithServer = true, ServerUrl = _server.BaseUrl, ServerClientVersion = "1.0.0" });
        store.Create(new GameInstance { Name = "Local", MinecraftVersion = "1.21.1" }); // sin servidor: no se consulta
        Publish("1.0.1", protectPacks: true);

        var updated = await CreateSync().CheckLinkedInstancesAsync(store, TestContext.Current.CancellationToken);

        var saved = updated.ShouldHaveSingleItem();
        saved.Id.ShouldBe(instance.Id);
        saved.HasUpdate.ShouldBeTrue();
        saved.ServerClientVersion.ShouldBe("1.0.0");  // la instalada no cambia: no se descargó nada
        saved.IsPacksLocked.ShouldBeTrue();          // la política sí se aplica
        store.LoadAll().Single(i => i.Id == instance.Id).HasUpdate.ShouldBeTrue();
        File.Exists(Path.Combine(store.GetDirectory(instance.Id), "versions", "1.21.1", "1.21.1.json")).ShouldBeFalse();

        // Sin cambios en el servidor, la siguiente consulta no reescribe nada.
        (await CreateSync().CheckLinkedInstancesAsync(store, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_version_only_counts_as_installed_after_a_successful_sync()
    {
        var instance = new GameInstance { Id = "srv", Name = "Servidor", SyncWithServer = true, ServerUrl = _server.BaseUrl, ServerClientVersion = "1.0.0" };

        Publish("1.0.1", serveFiles: false); // el manifest llega, pero la descarga falla
        (await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken)).Success.ShouldBeFalse();
        instance.ServerClientVersion.ShouldBe("1.0.0");

        Publish("1.0.1");
        (await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken)).Success.ShouldBeTrue();
        instance.ServerClientVersion.ShouldBe("1.0.1");
        instance.HasUpdate.ShouldBeFalse();
    }

    [Fact]
    public async Task Unreachable_servers_or_bad_urls_are_skipped()
    {
        var store = new InstanceStore(_dir.Combine("instances"));
        store.Create(new GameInstance { Name = "Caído", SyncWithServer = true, ServerUrl = "http://127.0.0.1:9" });
        store.Create(new GameInstance { Name = "Mal escrita", SyncWithServer = true, ServerUrl = "http://[nope" });

        (await CreateSync().CheckLinkedInstancesAsync(store, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}
