using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Tests.Infrastructure;
using KonnorLauncher.Core.Verification;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>
/// Reglas del servidor firmadas: el launcher las consulta al servidor, guarda solo la última política firmada
/// («última sesión») y, si falta, se modificó, caducó o es de otra clave, aplica lo más restrictivo.
/// </summary>
public sealed class ServerPolicyTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly LocalHttpServer _server = new();
    private readonly TempDir _dir = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private HttpClient _http = null!;

    public ValueTask InitializeAsync()
    {
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        _key.Dispose();
        await _server.DisposeAsync();
        _dir.Dispose();
    }

    private sealed class Factory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private static ServerPolicy Policy(bool alwaysOnline = false, bool lockUrl = false, bool released = false, string manifestSha256 = "",
        DateTimeOffset? issued = null) =>
        new("1.0.0", alwaysOnline, lockUrl, ProtectPacks: false, released, "mc.ejemplo.com", 6144,
            manifestSha256.Length == 64 ? manifestSha256 : new string('A', 64), issued ?? Now, (issued ?? Now) + ServerPolicyCrypto.Validity);

    // ── Firma ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_signed_policy_verifies_with_its_key()
    {
        var signed = ServerPolicyCrypto.Sign(Policy(), _key);

        var check = ServerPolicyCrypto.Verify(signed, pinnedKey: null, Now);

        check.Status.ShouldBe(PolicyStatus.Valid);
        check.Policy!.AlwaysOnline.ShouldBeFalse();
        check.Policy.AutoJoinAddress.ShouldBe("mc.ejemplo.com");
        check.Policy.RecommendedMemoryMb.ShouldBe(6144);
        ServerPolicyCrypto.Verify(signed, ServerPolicyCrypto.PublicKey(_key), Now).Status.ShouldBe(PolicyStatus.Valid);
    }

    [Fact]
    public void Changing_a_rule_breaks_the_signature()
    {
        var signed = ServerPolicyCrypto.Sign(Policy(alwaysOnline: true), _key);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(signed.Payload)).Replace("\"always_online\":true", "\"always_online\":false");
        var tampered = signed with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) };

        ServerPolicyCrypto.Verify(tampered, ServerPolicyCrypto.PublicKey(_key), Now).Status.ShouldBe(PolicyStatus.Invalid);
    }

    [Fact]
    public void A_policy_signed_by_another_server_is_rejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = ServerPolicyCrypto.Sign(Policy(), other);

        ServerPolicyCrypto.Verify(signed, ServerPolicyCrypto.PublicKey(_key), Now).Status.ShouldBe(PolicyStatus.KeyMismatch);
        // Y cambiar solo la clave del mensaje por la fijada no sirve: la firma no es de esa clave.
        ServerPolicyCrypto.Verify(signed with { Key = ServerPolicyCrypto.PublicKey(_key) }, ServerPolicyCrypto.PublicKey(_key), Now)
            .Status.ShouldBe(PolicyStatus.Invalid);
    }

    [Fact]
    public void A_policy_expires_after_its_validity()
    {
        var signed = ServerPolicyCrypto.Sign(Policy(), _key);

        ServerPolicyCrypto.Verify(signed, null, Now + ServerPolicyCrypto.Validity - TimeSpan.FromHours(1)).Status.ShouldBe(PolicyStatus.Valid);
        var expired = ServerPolicyCrypto.Verify(signed, null, Now + ServerPolicyCrypto.Validity + TimeSpan.FromHours(1));
        expired.Status.ShouldBe(PolicyStatus.Expired);
        expired.Policy.ShouldNotBeNull(); // la firma sigue siendo buena (sirve para la liberación)
    }

    [Fact]
    public void Missing_or_damaged_policies_are_invalid()
    {
        ServerPolicyCrypto.Verify(null, null, Now).Status.ShouldBe(PolicyStatus.Invalid);
        ServerPolicyCrypto.Verify(new SignedServerPolicy("no es base64", "x", "y"), null, Now).Status.ShouldBe(PolicyStatus.Invalid);
        ServerPolicyCrypto.Deserialize("no es json").ShouldBeNull();
    }

    [Fact]
    public void The_server_key_is_kept_between_starts()
    {
        var path = _dir.Combine("interno", "policy-key.pem");

        using var first = ServerPolicyCrypto.LoadOrCreateKey(path);
        using var second = ServerPolicyCrypto.LoadOrCreateKey(path);

        ServerPolicyCrypto.PublicKey(second).ShouldBe(ServerPolicyCrypto.PublicKey(first));
    }

    // ── Última sesión en la instancia ───────────────────────────────────────

    private GameInstance Linked(ServerPolicy policy)
    {
        var instance = new GameInstance { Id = "srv", SyncWithServer = true, ServerUrl = "http://servidor", SyncLocked = true };
        instance.AcceptSession(ServerPolicyCrypto.Sign(policy, _key), policy, Now);
        return instance;
    }

    [Fact]
    public void A_valid_session_applies_the_server_rules()
    {
        var instance = Linked(Policy(alwaysOnline: false, lockUrl: false));

        instance.ServerKey.ShouldBe(ServerPolicyCrypto.PublicKey(_key));
        instance.SessionStatus.ShouldBe(PolicyStatus.Valid);
        instance.ServerAlwaysOnline.ShouldBeFalse();
        instance.ServerLocksUrl.ShouldBeFalse();
        instance.ServerAutoJoin.ShouldBe("mc.ejemplo.com");
        instance.ServerMemoryMb.ShouldBe(6144);
        instance.AvailableClientVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public void Without_a_valid_session_the_strictest_rules_apply()
    {
        var instance = Linked(Policy(alwaysOnline: false, lockUrl: false));

        // Caducada (sin conexión demasiado tiempo)
        instance.ApplySession(Now + ServerPolicyCrypto.Validity + TimeSpan.FromDays(1));
        instance.SessionStatus.ShouldBe(PolicyStatus.Expired);
        instance.ServerAlwaysOnline.ShouldBeTrue();
        instance.ServerLocksUrl.ShouldBeTrue();
        instance.ServerProtectsPacks.ShouldBeTrue();
        instance.ServerAutoJoin.ShouldBeNull();

        // Borrada
        instance.ServerSession = null;
        instance.ApplySession(Now);
        instance.SessionStatus.ShouldBe(PolicyStatus.Invalid);
        instance.ServerAlwaysOnline.ShouldBeTrue();
    }

    [Fact]
    public void Editing_instance_json_does_not_unlock_the_instance()
    {
        var store = new InstanceStore(_dir.Combine("instances"), NullLogger<InstanceStore>.Instance);
        var instance = store.Create(new GameInstance { Name = "Srv", MinecraftVersion = "1.21.1", SyncWithServer = true, ServerUrl = "http://servidor", SyncLocked = true });
        instance.AcceptSession(ServerPolicyCrypto.Sign(Policy(alwaysOnline: true, lockUrl: true), _key), Policy(alwaysOnline: true, lockUrl: true), DateTimeOffset.Now);
        store.Save(instance);
        var file = Path.Combine(store.GetDirectory(instance.Id), InstanceStore.FileName);

        // Las reglas no están en el archivo: no hay campo que editar.
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json.ContainsKey("serverAlwaysOnline").ShouldBeFalse();
        json.ContainsKey("serverLocksUrl").ShouldBeFalse();

        // Añadirlos a mano no sirve, y cambiar la sesión firmada la invalida (lo más restrictivo).
        json["serverAlwaysOnline"] = false;
        var session = json["serverSession"]!.AsObject();
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(session["payload"]!.GetValue<string>()))
            .Replace("\"lock_server_url\":true", "\"lock_server_url\":false");
        session["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        File.WriteAllText(file, json.ToJsonString());

        var loaded = store.LoadAll().Single();
        loaded.SessionStatus.ShouldBe(PolicyStatus.Invalid);
        loaded.ServerAlwaysOnline.ShouldBeTrue();
        loaded.ServerLocksUrl.ShouldBeTrue();
        loaded.IsSyncLocked.ShouldBeTrue();
    }

    [Fact]
    public void A_release_survives_the_expiry_of_the_session()
    {
        var instance = Linked(Policy(released: true));
        instance.ServerReleased.ShouldBeTrue();

        // Una sesión posterior sin liberar, y además caducada: la liberación firmada sigue valiendo.
        var later = Policy(issued: Now + TimeSpan.FromDays(1));
        instance.AcceptSession(ServerPolicyCrypto.Sign(later, _key), later, Now + TimeSpan.FromDays(1));
        instance.ApplySession(Now + TimeSpan.FromDays(60));

        instance.ServerReleased.ShouldBeTrue();
        instance.IsSyncLocked.ShouldBeFalse();
    }

    // ── Sincronización con un servidor que firma ────────────────────────────

    private InstanceSync CreateSync() => new(new Factory(_http), metaDir: _dir.Combine("meta"));

    private GameInstance NewInstance() => new() { Id = "srv", Name = "Servidor", SyncWithServer = true, ServerUrl = _server.BaseUrl };

    /// <summary>Servidor con un archivo y su política firmada (por <paramref name="key"/>; por defecto, la del test).</summary>
    private byte[] Publish(bool alwaysOnline, ECDsa? key = null, string? signedSha256 = null)
    {
        var file = Encoding.UTF8.GetBytes("""{ "id": "1.21.1", "mainClass": "net.minecraft.client.main.Main" }""");
        var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            client_version = "1.0.0",
            always_online = alwaysOnline,
            files = new Dictionary<string, object>
            {
                ["versions/1.21.1/1.21.1.json"] = new { sha256 = Convert.ToHexString(SHA256.HashData(file)), size = file.Length },
            },
        }));
        var policy = ServerPolicy.FromManifest(ManifestParser.Parse(Encoding.UTF8.GetString(manifest)), manifest, DateTimeOffset.Now, ServerPolicyCrypto.Validity);
        if (signedSha256 is not null)
            policy = policy with { ManifestSha256 = signedSha256 };
        _server.Map("/client_manifest.json", manifest);
        _server.Map("/client/versions/1.21.1/1.21.1.json", file);
        _server.Map(ServerPolicyCrypto.Endpoint, ServerPolicyCrypto.Serialize(ServerPolicyCrypto.Sign(policy, key ?? _key)));
        return manifest;
    }

    [Fact]
    public async Task Sync_pins_the_server_key_and_keeps_the_signed_session()
    {
        Publish(alwaysOnline: false);
        var instance = NewInstance();

        var result = await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue(result.Message);
        instance.ServerKey.ShouldBe(ServerPolicyCrypto.PublicKey(_key));
        instance.SessionStatus.ShouldBe(PolicyStatus.Valid);
        instance.ServerAlwaysOnline.ShouldBeFalse();
        instance.IsSyncLocked.ShouldBeTrue();
    }

    [Fact]
    public async Task Refresh_asks_the_server_for_its_rules_without_the_manifest()
    {
        Publish(alwaysOnline: true);
        var instance = NewInstance();
        await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);
        Publish(alwaysOnline: false);
        while (_server.Requests.TryDequeue(out _)) { }

        (await CreateSync().RefreshPolicyAsync(instance, TestContext.Current.CancellationToken)).ShouldBeTrue();

        instance.ServerAlwaysOnline.ShouldBeFalse();
        _server.Requests.ShouldContain(r => r.Contains(ServerPolicyCrypto.Endpoint));
        _server.Requests.ShouldNotContain(r => r.Contains("client_manifest.json"));
    }

    [Fact]
    public async Task A_manifest_that_is_not_the_signed_one_is_not_installed()
    {
        Publish(alwaysOnline: false, signedSha256: new string('B', 64));
        var instance = NewInstance();

        var result = await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no es la que firmó");
        instance.ServerKey.ShouldBeNull();
        File.Exists(Path.Combine(_dir.Combine("instances"), "srv", "versions", "1.21.1", "1.21.1.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task Another_server_at_the_same_address_is_rejected()
    {
        Publish(alwaysOnline: false);
        var instance = NewInstance();
        await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);

        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish(alwaysOnline: false, key: impostor);
        var result = await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("identidad del servidor");
        instance.ServerKey.ShouldBe(ServerPolicyCrypto.PublicKey(_key));
        (await CreateSync().RefreshPolicyAsync(instance, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_linked_server_cannot_stop_signing()
    {
        Publish(alwaysOnline: false);
        var instance = NewInstance();
        await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);

        // Otro servidor en su dirección, sin firma (/api/policy da 404)
        await using var unsigned = new LocalHttpServer();
        instance.ServerUrl = unsigned.BaseUrl;
        unsigned.Map("/client_manifest.json", """{ "always_online": false, "files": {} }""");

        var result = await CreateSync().SyncAsync(instance, _dir.Combine("instances"), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("ya no firma");
    }
}
