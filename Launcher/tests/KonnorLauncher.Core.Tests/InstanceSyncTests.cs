using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Tests.Infrastructure;
using KonnorLauncher.Core.Verification;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Política de seguridad del servidor: vínculo, liberación y "siempre en línea".</summary>
public sealed class InstanceSyncTests : IAsyncLifetime
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

    /// <summary>Servidor con una versión vanilla y la política indicada (null = no se publica).</summary>
    private void Publish(bool? alwaysOnline = null, bool? released = null, bool? lockUrl = null)
    {
        var json = Encoding.UTF8.GetBytes("""{ "id": "1.21.1", "mainClass": "net.minecraft.client.main.Main" }""");
        var manifest = new Dictionary<string, object>
        {
            ["client_version"] = "1.0.0",
            ["files"] = new Dictionary<string, object>
            {
                ["versions/1.21.1/1.21.1.json"] = new { sha256 = Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant(), size = json.Length },
            },
        };
        if (alwaysOnline is { } online)
            manifest["always_online"] = online;
        if (released is { } r)
            manifest["released"] = r;
        if (lockUrl is { } l)
            manifest["lock_server_url"] = l;
        _server.Map("/client_manifest.json", JsonSerializer.Serialize(manifest));
        _server.Map("/client/versions/1.21.1/1.21.1.json", json);
    }

    private GameInstance NewInstance(string url) => new() { Id = "srv", Name = "Servidor", SyncWithServer = true, ServerUrl = url };

    [Fact]
    public async Task Successful_sync_links_the_instance_and_remembers_the_policy()
    {
        Publish(alwaysOnline: false);
        var instance = NewInstance(_server.BaseUrl);

        var result = await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.VersionId.ShouldBe("1.21.1");
        instance.SyncLocked.ShouldBeTrue();
        instance.IsSyncLocked.ShouldBeTrue();
        instance.ServerAlwaysOnline.ShouldBeFalse();
    }

    [Fact]
    public async Task Older_servers_without_policy_keep_the_current_behaviour()
    {
        Publish();
        var instance = NewInstance(_server.BaseUrl);

        await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);

        instance.ServerAlwaysOnline.ShouldBeTrue(); // sin servidor no se juega, como hasta ahora
        instance.IsSyncLocked.ShouldBeTrue();
    }

    [Fact]
    public async Task Released_server_unlocks_the_instance_for_good()
    {
        Publish(released: true);
        var instance = NewInstance(_server.BaseUrl);
        instance.SyncLocked = true;

        await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);
        instance.ServerReleased.ShouldBeTrue();
        instance.IsSyncLocked.ShouldBeFalse();

        // Un manifest posterior que ya no lo anuncie no vuelve a vincularla.
        Publish(released: false);
        await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);
        instance.ServerReleased.ShouldBeTrue();
        instance.IsSyncLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task Url_lock_follows_the_server_and_is_reversible()
    {
        var instance = NewInstance(_server.BaseUrl);

        Publish(); // servidor anterior: la dirección queda bloqueada, como hasta ahora
        await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);
        instance.IsUrlLocked.ShouldBeTrue();

        Publish(lockUrl: false);
        (await CreateSync().RefreshPolicyAsync(instance, TestContext.Current.CancellationToken)).ShouldBeTrue();
        instance.IsUrlLocked.ShouldBeFalse();

        Publish(lockUrl: true); // reversible: el servidor puede volver a bloquearla
        await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);
        instance.IsUrlLocked.ShouldBeTrue();
    }

    [Fact]
    public void New_instances_never_lock_the_url_before_their_first_sync()
    {
        var instance = new GameInstance { SyncWithServer = true, ServerUrl = "http://x" };
        instance.IsUrlLocked.ShouldBeFalse(); // aún no habló con ningún servidor
    }

    [Fact]
    public async Task Unreachable_server_is_reported_as_such()
    {
        var instance = NewInstance("http://127.0.0.1:9"); // nadie escucha en el puerto 9

        var result = await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ServerUnreachable.ShouldBeTrue();
        instance.SyncLocked.ShouldBeFalse();
    }

    [Fact]
    public void Manifest_policy_flags_are_parsed()
    {
        var manifest = ManifestParser.Parse("""{ "files": {}, "always_online": false, "released": true, "lock_server_url": false, "delete_unauthorized": "si" }""");
        manifest.LockServerUrl.ShouldBe(false);
        manifest.AlwaysOnline.ShouldBe(false);
        manifest.Released.ShouldBe(true);
        manifest.DeleteUnauthorized.ShouldBeNull(); // solo cuenta un booleano
    }
}
