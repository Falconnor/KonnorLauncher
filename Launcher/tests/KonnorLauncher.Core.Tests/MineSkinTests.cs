using KonnorLauncher.Core.Players;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Galería de skins de MineSkin (contra un servidor HTTP local).</summary>
public sealed class MineSkinTests : IAsyncLifetime
{
    private const string HashA = "a3b895662b59a489bcfdae452142d1c91ecb116f1a48f0442ea40d7b2888f38a";
    private const string HashB = "34c67eb4f7664040146736a36d0f762c23fb08a8a403df7dac8d5888286e4617";

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

    private MineSkinClient Client() => new(new Factory(_http), baseUrl: _server.BaseUrl + "/v2", texturesUrl: _server.BaseUrl + "/texture");

    [Fact]
    public async Task List_ReadsSkinsAndNextCursor_AndDropsInvalidTextures()
    {
        _server.Map("/v2/skins?size=48&filter=steve%20rojo", $$$$"""
            {"success":true,"skins":[
              {"uuid":"u1","shortId":"x","name":"Steve rojo","texture":"{{{{HashA}}}}","timestamp":10},
              {"uuid":"u2","name":null,"texture":"{{{{HashB}}}}","timestamp":9},
              {"uuid":"u3","name":"malo","texture":"../../evil","timestamp":8}],
             "pagination":{"current":{},"next":{"after":"u3"}}}
            """);

        var page = await Client().ListAsync("steve rojo");

        page.Skins.Select(s => s.Uuid).ShouldBe(["u1", "u2"]);
        page.Skins[0].Name.ShouldBe("Steve rojo");
        page.Skins[1].Name.ShouldBeNull();
        page.NextAfter.ShouldBe("u3");
    }

    [Fact]
    public async Task List_WithoutNext_HasNoCursor()
    {
        _server.Map("/v2/skins?size=48&after=u3", """{"skins":[],"pagination":{"current":{}}}""");

        var page = await Client().ListAsync(after: "u3");

        page.Skins.ShouldBeEmpty();
        page.NextAfter.ShouldBeNull();
    }

    [Fact]
    public async Task Get_ReadsVariantAndTexture()
    {
        _server.Map("/v2/skins/u1", $$$$"""
            {"success":true,"skin":{"uuid":"u1","name":"Alex","visibility":"public","variant":"slim",
             "texture":{"data":{"value":"x","signature":"y"},"hash":{"skin":"{{{{HashA}}}}"},"url":{"skin":"https://textures.minecraft.net/texture/{{{{HashA}}}}"}},
             "views":7}}
            """);

        var skin = await Client().GetAsync("u1");

        skin.Slim.ShouldBeTrue();
        skin.Texture.ShouldBe(HashA);
        skin.Name.ShouldBe("Alex");
        skin.Views.ShouldBe(7);
    }

    [Fact]
    public async Task Get_Missing_ThrowsReadableError()
    {
        var ex = await Should.ThrowAsync<MineSkinException>(() => Client().GetAsync("nada"));
        ex.Message.ShouldBe("La skin no existe.");
    }

    [Fact]
    public async Task DownloadTexture_SavesFile()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        _server.Map("/texture/" + HashA, png);
        var path = _dir.Combine("skins", "a.png");

        await Client().DownloadTextureAsync(HashA, path);

        File.ReadAllBytes(path).ShouldBe(png);
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadTexture_RejectsInvalidHash_WithoutRequest()
    {
        await Should.ThrowAsync<MineSkinException>(() => Client().DownloadTextureAsync("../secreto", _dir.Combine("x.png")));
        _server.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HashA, true)]
    [InlineData("ABC", false)]
    [InlineData("a3b8/../x", false)]
    [InlineData(null, false)]
    public void IsTextureHash(string? value, bool expected) => MineSkinClient.IsTextureHash(value).ShouldBe(expected);
}
