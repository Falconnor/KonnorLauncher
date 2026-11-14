using System.Net;
using System.Text;
using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Hosting.Exaroton;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Hosting de terceros: la API de exaroton (con respuestas como las de su documentación) y la lista guardada.</summary>
public class ThirdPartyTests
{
    private const string ServersJson = """
        {"success":true,"error":null,"data":[
          {"id":"EwYiY9IAMtQBTb6U","name":"Amigos","address":"amigos.exaroton.me","motd":"Hola","status":1,
           "host":"host.exaroton.me","port":25565,"players":{"max":20,"count":2,"list":["Steve","Alex"]},
           "software":{"id":"kb4p09ABvLjxzedx","name":"Paper","version":"1.21.1","type":"vanilla","launcher":null},
           "shared":false,"permissions":null},
          {"id":"Xx","name":"Pruebas","address":"pruebas.exaroton.me","motd":"","status":7,"players":{"max":10,"count":0},"software":null,"shared":true}
        ]}
        """;

    [Fact]
    public async Task Servers_are_read_with_their_status_and_players()
    {
        var handler = new FakeHandler(_ => Json(ServersJson));
        var client = new ExarotonClient(new Factory(handler), " token-de-prueba ");

        var servers = await client.GetServersAsync(TestContext.Current.CancellationToken);

        servers.Count.ShouldBe(2);
        var amigos = servers[0];
        (amigos.Name, amigos.Address, amigos.Status).ShouldBe(("Amigos", "amigos.exaroton.me", ExarotonStatus.Online));
        amigos.Players!.Count.ShouldBe(2);
        amigos.Players.List.ShouldBe(["Steve", "Alex"]);
        amigos.SoftwareText.ShouldBe("Paper 1.21.1");
        amigos.IsRunning.ShouldBeTrue();
        servers[1].Status.ShouldBe(ExarotonStatus.Crashed);
        servers[1].IsRunning.ShouldBeFalse(); // se puede volver a iniciar
        servers[1].SoftwareText.ShouldBe("exaroton");

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe("https://api.exaroton.com/v1/servers/");
        request.Authorization.ShouldBe("Bearer token-de-prueba");
    }

    [Fact]
    public async Task Power_commands_and_console_use_the_documented_routes()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/logs/")
            ? Json("""{"success":true,"error":null,"data":{"content":"[12:00:00] Done!"}}""")
            : Json("""{"success":true,"error":null,"data":null}"""));
        var client = new ExarotonClient(new Factory(handler), "t");
        var ct = TestContext.Current.CancellationToken;

        await client.StartAsync("abc", ct);
        await client.StopAsync("abc", ct);
        await client.RestartAsync("abc", ct);
        await client.CommandAsync("abc", "say Hola", ct);
        (await client.GetLogsAsync("abc", ct)).ShouldBe("[12:00:00] Done!");
        await client.UploadFileAsync("abc", @"mods\sodium 1.jar", new MemoryStream([1, 2, 3]), ct);

        handler.Requests.Select(r => $"{r.Method} {r.Uri}").ShouldBe([
            "GET https://api.exaroton.com/v1/servers/abc/start/",
            "GET https://api.exaroton.com/v1/servers/abc/stop/",
            "GET https://api.exaroton.com/v1/servers/abc/restart/",
            "POST https://api.exaroton.com/v1/servers/abc/command/",
            "GET https://api.exaroton.com/v1/servers/abc/logs/",
            "PUT https://api.exaroton.com/v1/servers/abc/files/data/mods/sodium%201.jar/",
        ]);
        handler.Requests[3].Body.ShouldBe("""{"command":"say Hola"}""");
        handler.Requests[5].ContentType.ShouldBe("application/octet-stream");
    }

    [Fact]
    public async Task Errors_from_exaroton_are_shown_with_their_message()
    {
        var failing = new ExarotonClient(new Factory(new FakeHandler(_ =>
            Json("""{"success":false,"error":"Server is not offline","data":null}""", HttpStatusCode.BadRequest))), "t");
        (await Should.ThrowAsync<ExarotonException>(() => failing.StartAsync("abc", TestContext.Current.CancellationToken)))
            .Message.ShouldBe("Server is not offline");

        var badToken = new ExarotonClient(new Factory(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))), "t");
        (await Should.ThrowAsync<ExarotonException>(() => badToken.GetAccountAsync(TestContext.Current.CancellationToken)))
            .Message.ShouldBe("El token de exaroton no es válido.");
    }

    [Fact]
    public void Entries_are_saved_and_removed()
    {
        using var dir = new TempDir();
        var store = new ThirdPartyStore(dir.Path);
        var panel = new ThirdPartyEntry { Provider = ThirdPartyProvider.Aternos, Name = "Survival", PanelUrl = "https://aternos.org/servers/", JoinAddress = "survival.aternos.me" };
        var account = new ThirdPartyEntry { Provider = ThirdPartyProvider.Exaroton, Name = "Mi cuenta", ProtectedToken = "cifrado" };

        store.Add(panel);
        store.Add(account);
        var loaded = store.LoadAll();
        loaded.Select(e => e.Provider).ShouldBe([ThirdPartyProvider.Aternos, ThirdPartyProvider.Exaroton]);
        loaded[0].JoinAddress.ShouldBe("survival.aternos.me");
        File.ReadAllText(dir.Combine("terceros.json")).ShouldContain("\"Aternos\"");

        store.Remove(panel.Id);
        store.LoadAll().ShouldHaveSingleItem().Id.ShouldBe(account.Id);
    }

    [Theory]
    [InlineData("https://aternos.org/servers/", true)]
    [InlineData("http://192.168.1.10:8080", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows", false)]
    [InlineData("panel", false)]
    public void Only_web_panels_can_be_opened(string url, bool valid) =>
        ThirdPartyEntry.IsValidPanel(url, out _).ShouldBe(valid);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Captured(string Method, string Uri, string? Authorization, string? Body, string? ContentType);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Captured(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Content is StreamContent ? null : body, request.Content?.Headers.ContentType?.MediaType));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
