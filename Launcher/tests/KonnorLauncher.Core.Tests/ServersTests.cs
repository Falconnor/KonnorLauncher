using System.Buffers.Binary;
using System.Text;
using KonnorLauncher.Core.Servers;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Sección Servidores: direcciones, directorio de Modrinth, estado en vivo, servers.dat y «Mis servidores».</summary>
public sealed class ServersTests : IAsyncLifetime
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

    // ── Direcciones ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("play.example.com", "play.example.com", 25565)]
    [InlineData("  Play.Example.COM.  ", "play.example.com", 25565)]
    [InlineData("mc.server.net:25570", "mc.server.net", 25570)]
    [InlineData("95.164.127.81", "95.164.127.81", 25565)]
    [InlineData("localhost:1", "localhost", 1)]
    public void Valid_addresses_are_normalized(string text, string host, int port)
    {
        ServerAddress.TryParse(text, out var address).ShouldBeTrue();
        address.Host.ShouldBe(host);
        address.Port.ShouldBe(port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://play.example.com")]
    [InlineData("play example.com")]
    [InlineData("play.example.com:0")]
    [InlineData("play.example.com:70000")]
    [InlineData("play.example.com:abc")]
    [InlineData("../../etc")]
    [InlineData("-bad.com")]
    [InlineData(null)]
    public void Invalid_addresses_are_rejected(string? text) => ServerAddress.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Default_port_is_hidden_when_written()
    {
        new ServerAddress("a.com", 25565).ToString().ShouldBe("a.com");
        new ServerAddress("a.com", 25570).ToString().ShouldBe("a.com:25570");
    }

    // ── Directorio de Modrinth ───────────────────────────────────────────

    [Fact]
    public void Query_combines_filters_and_sort()
    {
        var text = Uri.UnescapeDataString(ServerDirectoryClient.BuildQueryString(new ServerQuery
        {
            Text = " smp ", Sort = ServerSort.Plays, GameVersion = "1.21.1", Category = "pokemon", Region = "europe",
            Language = "es", OfflineMode = true, Offset = 30, Limit = 30,
        }));

        text.ShouldStartWith("query=smp&");
        text.ShouldContain("""facets=[["project_types:minecraft_java_server"],["game_versions:1.21.1"],["categories:pokemon"],["categories:offline-mode"],["minecraft_server.region:europe"],["minecraft_server.languages:es"]]""");
        text.ShouldContain("index=minecraft_java_server.verified_plays_2w");
        text.ShouldContain("offset=30");
        text.ShouldContain("limit=30");
    }

    [Fact]
    public void Premium_only_excludes_offline_mode_servers()
    {
        var text = Uri.UnescapeDataString(ServerDirectoryClient.BuildQueryString(new ServerQuery { OfflineMode = false }));

        text.ShouldContain("""facets=[["project_types:minecraft_java_server"],["categories!=offline-mode"]]""");
    }

    [Fact]
    public void Default_query_sorts_by_players_online_without_extra_filters()
    {
        var text = Uri.UnescapeDataString(ServerDirectoryClient.BuildQueryString(new ServerQuery()));

        text.ShouldNotContain("query=");
        text.ShouldContain("""facets=[["project_types:minecraft_java_server"]]""");
        text.ShouldContain("index=minecraft_java_server.ping.data.players_online");
    }

    [Fact]
    public async Task Search_reads_address_players_region_and_modpack_and_skips_servers_without_address()
    {
        var query = new ServerQuery();
        _server.Map("/v3/search?" + ServerDirectoryClient.BuildQueryString(query), """
            {"hits":[
              {"project_id":"p1","slug":"aero","name":"Aero SMP","summary":"Create","icon_url":"https://cdn.modrinth.com/i.webp",
               "categories":["smp","offline-mode"],"game_versions":["1.21.1"],
               "minecraft_server":{"region":"europe","languages":["en","es"]},
               "minecraft_java_server":{"address":"Play.Aero.com","verified_plays_2w":3894,
                 "content":{"kind":"modpack","version_id":"v1","project_id":"p1"},
                 "ping":{"data":{"players_online":41,"players_max":300}}}},
              {"project_id":"p2","slug":"vanilla","name":"Vanilla","categories":[],"game_versions":["26.2"],
               "minecraft_java_server":{"address":"v.net:25570","content":{"kind":"vanilla","supported_game_versions":["26.2"],"recommended_game_version":"26.2"},"ping":null}},
              {"project_id":"p3","slug":"bad","name":"Sin dirección","minecraft_java_server":{"address":""}},
              {"project_id":"p4","slug":"bad2","name":"Dirección rara","minecraft_java_server":{"address":"http://x.com"}}
            ],"total_hits":2409}
            """);
        var client = new ServerDirectoryClient(new Factory(_http), baseUrl: _server.BaseUrl + "/v3");

        var page = await client.SearchAsync(query, TestContext.Current.CancellationToken);

        page.Total.ShouldBe(2409);
        page.Servers.Select(s => s.ProjectId).ShouldBe(["p1", "p2"]);
        var aero = page.Servers[0];
        aero.Address.ToString().ShouldBe("play.aero.com");
        aero.PlayersOnline.ShouldBe(41);
        aero.PlayersMax.ShouldBe(300);
        aero.IsOnline.ShouldBeTrue();
        aero.Region.ShouldBe("europe");
        aero.Languages.ShouldBe(["en", "es"]);
        aero.PlaysTwoWeeks.ShouldBe(3894);
        aero.ContentKind.ShouldBe(ServerContentKind.Modpack);
        aero.ModpackProjectId.ShouldBe("p1");
        aero.ModpackVersionId.ShouldBe("v1");
        aero.AllowsOfflinePlayers.ShouldBeTrue();
        aero.PageUrl.ShouldBe("https://modrinth.com/server/aero");
        var vanilla = page.Servers[1];
        vanilla.Address.Port.ShouldBe(25570);
        vanilla.IsOnline.ShouldBeFalse();
        vanilla.ContentKind.ShouldBe(ServerContentKind.Vanilla);
        vanilla.RecommendedVersion.ShouldBe("26.2");
        vanilla.AllowsOfflinePlayers.ShouldBeFalse();
    }

    [Fact]
    public async Task Search_without_answer_throws_a_readable_error()
    {
        var client = new ServerDirectoryClient(new Factory(_http), baseUrl: _server.BaseUrl + "/v3");

        var error = await Should.ThrowAsync<Modrinth.ModrinthException>(() => client.SearchAsync(new ServerQuery(), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("Modrinth");
    }

    // ── Estado en vivo ───────────────────────────────────────────────────

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public async Task Status_reads_players_version_motd_and_icon()
    {
        _server.Map("/v2/status/java/play.aero.com", $$"""
            {"online":true,"host":"play.aero.com","port":25565,"version":{"name_clean":"Velocity 1.7.2"},
             "players":{"online":40,"max":300,"list":[]},"motd":{"raw":"x","clean":"  Hola  "},
             "icon":"data:image/png;base64,{{Convert.ToBase64String(Png)}}"}
            """);
        var client = new ServerStatusClient(new Factory(_http), baseUrl: _server.BaseUrl + "/v2");

        var status = await client.GetAsync(new ServerAddress("play.aero.com", 25565), TestContext.Current.CancellationToken);

        status.ShouldNotBeNull();
        status.Online.ShouldBeTrue();
        status.PlayersOnline.ShouldBe(40);
        status.PlayersMax.ShouldBe(300);
        status.Version.ShouldBe("Velocity 1.7.2");
        status.Motd.ShouldBe("Hola");
        status.Icon.ShouldBe(Png);
    }

    [Fact]
    public async Task Status_of_an_offline_server_and_of_an_unreachable_api()
    {
        _server.Map("/v2/status/java/off.com%3A25570", """{"online":false,"host":"off.com","port":25570}""");
        var client = new ServerStatusClient(new Factory(_http), baseUrl: _server.BaseUrl + "/v2");

        var offline = await client.GetAsync(new ServerAddress("off.com", 25570), TestContext.Current.CancellationToken);
        var unknown = await client.GetAsync(new ServerAddress("nadie.com", 25565), TestContext.Current.CancellationToken);

        offline.ShouldNotBeNull();
        offline.Online.ShouldBeFalse();
        unknown.ShouldBeNull(); // la API no respondió bien: no se sabe
    }

    [Theory]
    [InlineData(null)]
    [InlineData("data:image/jpeg;base64,AAAA")]
    [InlineData("data:image/png;base64,no-es-base64")]
    [InlineData("data:image/png;base64,AAAA")] // no es un PNG
    public void Only_png_icons_are_accepted(string? icon) => ServerStatusClient.DecodeIcon(icon).ShouldBeNull();

    // ── Ping ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, PingQuality.Good)]
    [InlineData(80, PingQuality.Good)]
    [InlineData(81, PingQuality.Fair)]
    [InlineData(120, PingQuality.Fair)]
    [InlineData(121, PingQuality.Poor)]
    [InlineData(900, PingQuality.Poor)]
    public void Ping_quality_thresholds(int ms, PingQuality expected) => ServerPing.Quality(ms).ShouldBe(expected);

    [Fact]
    public async Task Ping_measures_the_connection_to_a_listening_port()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

            var ms = await ServerPing.MeasureAsync(new ServerAddress("127.0.0.1", port), ct: TestContext.Current.CancellationToken);

            ms.ShouldNotBeNull();
            ms.Value.ShouldBeInRange(1, 1000);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Ping_to_a_closed_port_is_null()
    {
        // Un puerto libre: se abre y se cierra para tener uno en el que nadie escucha
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var ms = await ServerPing.MeasureAsync(new ServerAddress("127.0.0.1", port), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        ms.ShouldBeNull();
    }

    // ── servers.dat ──────────────────────────────────────────────────────

    [Fact]
    public void Added_server_goes_first_and_is_not_duplicated()
    {
        var game = _dir.Combine("game");

        ServersDat.Add(game, "Uno", new ServerAddress("uno.com", 25565)).ShouldBeTrue();
        ServersDat.Add(game, "Dos", new ServerAddress("dos.com", 25570)).ShouldBeTrue();
        ServersDat.Add(game, "Otra vez", new ServerAddress("uno.com", 25565)).ShouldBeFalse();

        ServersDat.Read(game).ShouldBe([new ServersDat.Entry("Dos", "dos.com:25570"), new ServersDat.Entry("Uno", "uno.com")]);
    }

    [Fact]
    public void Existing_servers_and_unknown_tags_are_kept()
    {
        var game = _dir.Combine("game");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, ServersDat.FileName), ExistingServersDat());

        ServersDat.Add(game, "Nuevo", new ServerAddress("nuevo.com", 25565)).ShouldBeTrue();

        ServersDat.Read(game).Select(e => e.Address).ShouldBe(["nuevo.com", "Viejo.com:25566"]);
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(game, ServersDat.FileName)));
        raw.ShouldContain("icon");
        raw.ShouldContain("iVBORfake");
        raw.ShouldContain("acceptTextures");
        raw.ShouldContain("extra");
        // La misma dirección escrita distinto cuenta como la misma
        ServersDat.Add(game, "x", new ServerAddress("viejo.com", 25566)).ShouldBeFalse();
    }

    [Fact]
    public void Damaged_file_counts_as_empty()
    {
        var game = _dir.Combine("game");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, ServersDat.FileName), [10, 0, 0, 9, 0, 7, 0x73, 0x65, 0x72, 0x76, 0x65, 0x72, 0x73, 10, 0x7F, 0xFF, 0xFF, 0xFF]);

        ServersDat.Read(game).ShouldBeEmpty();
        ServersDat.Add(game, "Uno", new ServerAddress("uno.com", 25565)).ShouldBeTrue();
        ServersDat.Read(game).Count.ShouldBe(1);
    }

    /// <summary>NBT como lo escribe el juego: un servidor con icono y aceptación de texturas, y una etiqueta extra.</summary>
    private static byte[] ExistingServersDat()
    {
        using var s = new MemoryStream();
        void Str(string v)
        {
            var b = Encoding.UTF8.GetBytes(v);
            s.WriteByte((byte)(b.Length >> 8));
            s.WriteByte((byte)b.Length);
            s.Write(b);
        }

        void Int(int v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, v);
            s.Write(b);
        }

        s.WriteByte(10); Str("");                       // raíz
        s.WriteByte(9); Str("servers"); s.WriteByte(10); Int(1);
        s.WriteByte(8); Str("name"); Str("Viejo");
        s.WriteByte(8); Str("ip"); Str("Viejo.com:25566");
        s.WriteByte(8); Str("icon"); Str("iVBORfake");
        s.WriteByte(1); Str("acceptTextures"); s.WriteByte(1);
        s.WriteByte(0);                                 // fin del servidor
        s.WriteByte(3); Str("extra"); Int(7);
        s.WriteByte(0);                                 // fin de la raíz
        return s.ToArray();
    }

    // ── Mis servidores ───────────────────────────────────────────────────

    [Fact]
    public void Saved_servers_are_normalized_updated_and_removed()
    {
        var library = new ServerLibrary(_dir.Combine("data"));

        library.Add(" Play.Uno.com ", null).ShouldBe(library.Saved()[0]);
        library.Add("dos.com:25570", "  Dos  ");
        library.Add("play.uno.com:25565", "Uno con nombre"); // la misma: se actualiza el nombre

        var saved = new ServerLibrary(_dir.Combine("data")).Saved(); // otra instancia: lo lee del disco
        saved.Select(s => (s.Name, s.Address)).ShouldBe([("Uno con nombre", "play.uno.com"), ("Dos", "dos.com:25570")]);
        library.Remove("play.uno.com").ShouldBeTrue();
        library.Remove("play.uno.com").ShouldBeFalse();
        library.Saved().Count.ShouldBe(1);
        Should.Throw<ArgumentException>(() => library.Add("http://malo", null));
    }

    [Fact]
    public void Linked_instances_are_remembered_and_a_damaged_file_starts_empty()
    {
        var library = new ServerLibrary(_dir.Combine("data"));
        library.LinkedInstance("p1").ShouldBeNull();

        library.LinkInstance("p1", "aero-smp");

        new ServerLibrary(_dir.Combine("data")).LinkedInstance("p1").ShouldBe("aero-smp");
        File.WriteAllText(_dir.Combine("data", ServerLibrary.FileName), "{ roto");
        library.Saved().ShouldBeEmpty();
        library.LinkedInstance("p1").ShouldBeNull();
    }
}
