using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KonnorLauncher.Core.Progress;
using KonnorLauncher.Core.Tests.Infrastructure;
using KonnorLauncher.Core.Verification;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Verificador: casos mínimos de compatibilidad contra un servidor HTTP local real.</summary>
public sealed class VerificationTests : IAsyncLifetime
{
    private readonly LocalHttpServer _server = new();
    private readonly TempDir _game = new();
    private readonly Dictionary<string, object> _files = new();
    private readonly Dictionary<string, object> _blocks = new();
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
        _game.Dispose();
    }

    // ── Utilidades para montar el "servidor" ─────────────────────────────

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Publica un archivo suelto (ruta del manifest + URL codificada como la espera el servidor).</summary>
    private void AddFile(string path, string content, string? encodedPath = null, string? overrideSha = null)
    {
        var data = Encoding.UTF8.GetBytes(content);
        _files[path] = new { sha256 = overrideSha ?? Sha(data), size = data.Length };
        _server.Map("/client/" + (encodedPath ?? path), data);
    }

    /// <summary>Publica un archivo suelto binario.</summary>
    private void AddFile(string path, byte[] data)
    {
        _files[path] = new { sha256 = Sha(data), size = data.Length };
        _server.Map("/client/" + path, data);
    }

    /// <summary>Bytes pseudoaleatorios reproducibles (un archivo "grande" para probar reanudaciones).</summary>
    private static byte[] RandomBytes(int length, int seed = 42)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Crea una descarga parcial como la que dejaría una sesión interrumpida.</summary>
    private string WritePartial(string key, byte[] fullData, byte[] partialContent)
    {
        var dir = Directory.CreateDirectory(_game.Combine(ClientVerifier.DownloadsFolderName)).FullName;
        var path = Path.Combine(dir, ClientVerifier.PartialFileName(key, Sha(fullData)));
        File.WriteAllBytes(path, partialContent);
        return path;
    }

    /// <summary>Publica un bloque ZIP con varios archivos.</summary>
    private void AddBlock(string name, params (string Path, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }

        var bytes = ms.ToArray();
        _blocks[name] = new
        {
            sha256 = Sha(bytes),
            size = bytes.Length,
            files = entries.ToDictionary(e => e.Path, e => (object)new { sha256 = Sha(Encoding.UTF8.GetBytes(e.Content)), size = Encoding.UTF8.GetByteCount(e.Content) }),
        };
        _server.Map("/client/packages/" + name, bytes);
    }

    private void PublishManifest(bool includeBlocks = true)
    {
        var manifest = new Dictionary<string, object> { ["client_version"] = "1.2.3", ["files"] = _files };
        if (includeBlocks)
            manifest["asset_blocks"] = _blocks;
        _server.Map("/client_manifest.json", JsonSerializer.Serialize(manifest));
    }

    private ClientVerifier CreateVerifier() => new(_http) { RetryDelay = TimeSpan.FromMilliseconds(10) };

    private VerifierOptions Options(bool dryRun = false, bool deleteUnauthorized = true, string? url = null) => new()
    {
        GameDir = _game.Path,
        ServerUrl = url ?? _server.BaseUrl + "/",
        EnableVerify = true,
        DeleteUnauthorized = deleteUnauthorized,
        DryRun = dryRun,
    };

    private string GameFile(string relative) => _game.Combine(relative.Split('/'));

    // ── Casos de compatibilidad ─────────────────────────────────────────────────

    [Fact]
    public async Task Case01_empty_client_downloads_everything_and_second_run_repairs_nothing()
    {
        AddFile("mods/a.jar", "AAAA");
        AddFile("versions/x/x.json", "{\"id\":\"x\"}");
        AddBlock("assets_001.zip", ("assets/objects/ab/abc", "asset-1"), ("assets/indexes/1.json", "{}"));
        PublishManifest();
        var statuses = new List<LauncherProgress>();

        var first = await CreateVerifier().VerifyAsync(Options(), new SyncProgress(statuses.Add), TestContext.Current.CancellationToken);

        first.Success.ShouldBeTrue();
        first.Status.ShouldBe("verificacion exitosa");
        first.FilesToRepair.ShouldBe(2);
        first.BlocksToRepair.ShouldBe(1);
        File.ReadAllText(GameFile("mods/a.jar")).ShouldBe("AAAA");
        File.ReadAllText(GameFile("assets/objects/ab/abc")).ShouldBe("asset-1");
        statuses.ShouldContain(s => s.Message.StartsWith("descargando archivos: ", StringComparison.Ordinal) && s.Message.EndsWith(" MB", StringComparison.Ordinal));
        statuses[^1].Message.ShouldBe("verificacion exitosa");
        Directory.GetDirectories(_game.Path, "verify-*").ShouldBeEmpty(); // temporal borrado
        File.Exists(GameFile("assets_001.zip")).ShouldBeFalse();

        var second = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);
        second.FilesToRepair.ShouldBe(0);
        second.BlocksToRepair.ShouldBe(0);
    }

    [Fact]
    public async Task Case02_file_with_different_size_is_downloaded_again()
    {
        AddFile("mods/a.jar", "contenido-correcto");
        PublishManifest();
        _game.Write("mods/a.jar", "corto");

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.FilesToRepair.ShouldBe(1);
        File.ReadAllText(GameFile("mods/a.jar")).ShouldBe("contenido-correcto");
    }

    [Fact]
    public async Task Case02b_same_size_file_is_assumed_valid()
    {
        AddFile("mods/a.jar", "1234");
        PublishManifest();
        _game.Write("mods/a.jar", "abcd"); // mismo tamaño, distinto contenido: los archivos existentes no se hashean a propósito

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.FilesToRepair.ShouldBe(0);
        File.ReadAllText(GameFile("mods/a.jar")).ShouldBe("abcd");
    }

    [Fact]
    public async Task Case03_wrong_hash_fails_without_cleanup_or_partial_file()
    {
        AddFile("mods/a.jar", "AAAA", overrideSha: new string('0', 64));
        PublishManifest();
        _game.Write("mods/no-listado.jar", "x");

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe("verificacion fallida");
        File.Exists(GameFile("mods/a.jar")).ShouldBeFalse();       // nada a medias en destino
        File.Exists(GameFile("mods/no-listado.jar")).ShouldBeTrue(); // la limpieza no se ejecutó
        Directory.GetDirectories(_game.Path, "verify-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task Case04_server_down_reports_servidor_no_responde()
    {
        var statuses = new List<LauncherProgress>();
        var result = await CreateVerifier().VerifyAsync(Options(url: "http://127.0.0.1:1"), new SyncProgress(statuses.Add), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe("servidor no responde");
        statuses[^1].Message.ShouldBe("servidor no responde");
    }

    [Fact]
    public async Task Case04b_invalid_json_reports_servidor_no_responde()
    {
        _server.Map("/client_manifest.json", "{ no es json");
        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);
        result.Status.ShouldBe("servidor no responde");
    }

    [Fact]
    public async Task Case05_verify_disabled_succeeds_without_network()
    {
        PublishManifest();
        var result = await CreateVerifier().VerifyAsync(Options() with { EnableVerify = false }, null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe("verificacion exitosa");
        _server.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("C:/x")]
    [InlineData("/abs")]
    [InlineData("a//b")]
    [InlineData("./a")]
    [InlineData("mods/a.jar:stream")]
    [InlineData("mods/con?.jar")]
    [InlineData("mods/fin.")]
    [InlineData("\\\\server\\share\\x")]
    public async Task Case06_dangerous_paths_are_rejected_before_touching_disk(string path)
    {
        _files[path] = new { sha256 = new string('a', 64), size = 1 };
        PublishManifest();

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe("verificacion fallida");
        Directory.EnumerateFileSystemEntries(_game.Path).ShouldBeEmpty();
        _server.Requests.ShouldBe(["/client_manifest.json"]);
    }

    [Fact]
    public async Task Case07_cleanup_removes_unlisted_jar_and_keeps_saves()
    {
        AddFile("mods/a.jar", "AAAA");
        AddBlock("b.zip", ("assets/x", "x"));
        PublishManifest();
        _game.Write("mods/intruso.jar", "x");
        _game.Write("mods/A.JAR", "AAAA");               // NTFS no distingue mayúsculas: es el mismo archivo
        _game.Write("versions/viejo/viejo.json", "{}");
        _game.Write("saves/mundo/level.dat", "mundo");
        _game.Write("options.txt", "opciones");
        _game.Write("config/mod.toml", "cfg");

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.Exists(GameFile("mods/intruso.jar")).ShouldBeFalse();
        File.Exists(GameFile("versions/viejo/viejo.json")).ShouldBeFalse();
        File.Exists(GameFile("mods/a.jar")).ShouldBeTrue();
        File.Exists(GameFile("assets/x")).ShouldBeTrue();
        File.Exists(GameFile("saves/mundo/level.dat")).ShouldBeTrue();
        File.Exists(GameFile("options.txt")).ShouldBeTrue();
        File.Exists(GameFile("config/mod.toml")).ShouldBeTrue();
    }

    [Fact]
    public async Task Case07b_cleanup_disabled_keeps_everything()
    {
        AddFile("mods/a.jar", "AAAA");
        PublishManifest();
        _game.Write("mods/intruso.jar", "x");

        await CreateVerifier().VerifyAsync(Options(deleteUnauthorized: false), null, TestContext.Current.CancellationToken);

        File.Exists(GameFile("mods/intruso.jar")).ShouldBeTrue();
    }

    [Fact]
    public async Task Case08_manifest_without_asset_blocks_works()
    {
        AddFile("mods/a.jar", "AAAA");
        PublishManifest(includeBlocks: false);

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.Exists(GameFile("mods/a.jar")).ShouldBeTrue();
    }

    [Fact]
    public async Task Case09_dry_run_counts_without_changing_disk()
    {
        AddFile("mods/a.jar", "AAAA");
        AddFile("mods/b.jar", "BBBB");
        AddBlock("b1.zip", ("assets/1", "1"));
        AddBlock("b2.zip", ("assets/2", "2"));
        PublishManifest();
        _game.Write("mods/b.jar", "BBBB");     // ya correcto
        _game.Write("assets/2", "2");          // bloque 2 completo
        _game.Write("mods/intruso.jar", "x");
        var statuses = new List<LauncherProgress>();

        var result = await CreateVerifier().VerifyAsync(Options(dryRun: true), new SyncProgress(statuses.Add), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.FilesToRepair.ShouldBe(1);
        result.BlocksToRepair.ShouldBe(1);
        statuses.Select(s => s.Message).ShouldContain("Simulación: 1 bloques y 1 archivos requieren reparación");
        File.Exists(GameFile("mods/a.jar")).ShouldBeFalse();
        File.Exists(GameFile("mods/intruso.jar")).ShouldBeTrue();
        _server.Requests.ShouldBe(["/client_manifest.json"]);
    }

    [Fact]
    public async Task Case10_spaces_and_non_ascii_are_encoded_like_python_quote()
    {
        // urllib.parse.quote("mods/mi mod ñ+(1).jar", safe='/') == "mods/mi%20mod%20%C3%B1%2B%281%29.jar"
        AddFile("mods/mi mod ñ+(1).jar", "X", encodedPath: "mods/mi%20mod%20%C3%B1%2B%281%29.jar");
        PublishManifest();

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.Exists(GameFile("mods/mi mod ñ+(1).jar")).ShouldBeTrue();
        _server.Requests.ShouldContain("/client/mods/mi%20mod%20%C3%B1%2B%281%29.jar");
    }

    [Fact]
    public async Task Missing_file_on_server_fails_and_reports_client_version()
    {
        _files["mods/falta.jar"] = new { sha256 = new string('a', 64), size = 3 };
        PublishManifest();
        string? received = null;
        var verifier = CreateVerifier();
        verifier.ClientVersionReceived = v => received = v;

        var result = await verifier.VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Status.ShouldBe("verificacion fallida");
        received.ShouldBe("1.2.3");
    }

    [Fact]
    public async Task Official_minecraft_dir_skips_cleanup_by_default()
    {
        // Solo comprueba la opción; no toca la .minecraft real.
        KonnorLauncher.Core.Paths.LauncherPaths.IsOfficialMinecraftDir(_game.Path).ShouldBeFalse();
        new VerifierOptions { GameDir = "x", ServerUrl = "y" }.SkipCleanupInOfficialDir.ShouldBeTrue();
        await Task.CompletedTask;
    }

    // ── Reanudación de descargas ─────────────────────────────────────────

    [Fact]
    public async Task Partial_download_is_resumed_with_range()
    {
        var data = RandomBytes(300_000);
        AddFile("mods/grande.jar", data);
        PublishManifest();
        WritePartial("mods/grande.jar", data, data[..100_000]); // sesión anterior interrumpida

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllBytes(GameFile("mods/grande.jar")).ShouldBe(data);
        _server.RangeRequests.ShouldContain(("/client/mods/grande.jar", "bytes=100000-"));
        Directory.Exists(_game.Combine(ClientVerifier.DownloadsFolderName)).ShouldBeFalse(); // se borra al terminar
    }

    [Fact]
    public async Task Corrupt_partial_is_discarded_and_downloaded_again()
    {
        var data = RandomBytes(300_000);
        AddFile("mods/grande.jar", data);
        PublishManifest();
        WritePartial("mods/grande.jar", data, RandomBytes(100_000, seed: 7)); // contenido que no corresponde

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllBytes(GameFile("mods/grande.jar")).ShouldBe(data);
        var requests = _server.RangeRequests.Where(r => r.Path == "/client/mods/grande.jar").ToList();
        requests.Count.ShouldBe(2);
        requests[0].Range.ShouldBe("bytes=100000-"); // intento de reanudar...
        requests[1].Range.ShouldBeNull();             // ...y descarga completa al ver el hash incorrecto
    }

    [Fact]
    public async Task Cut_connection_is_resumed_on_retry()
    {
        var data = RandomBytes(500_000);
        AddFile("mods/grande.jar", data);
        PublishManifest();
        _server.CutNextResponse("/client/mods/grande.jar", 200_000); // la red se corta a mitad

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllBytes(GameFile("mods/grande.jar")).ShouldBe(data);
        var requests = _server.RangeRequests.Where(r => r.Path == "/client/mods/grande.jar").ToList();
        requests.Count.ShouldBe(2);
        requests[1].Range.ShouldNotBeNull();
        requests[1].Range!.ShouldStartWith("bytes="); // el reintento solo pide lo que faltaba
    }

    [Fact]
    public async Task Cut_block_download_is_resumed_and_extracted()
    {
        AddBlock("b.zip", ("assets/objects/aa/aa1", new string('x', 50_000)), ("assets/objects/aa/aa2", "y"));
        PublishManifest();
        _server.CutNextResponse("/client/packages/b.zip", 100);

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllText(GameFile("assets/objects/aa/aa1")).Length.ShouldBe(50_000);
        _server.RangeRequests.ShouldContain(("/client/packages/b.zip", "bytes=100-"));
    }

    [Fact]
    public async Task Server_without_range_support_downloads_the_whole_file()
    {
        var data = RandomBytes(200_000);
        AddFile("mods/grande.jar", data);
        PublishManifest();
        WritePartial("mods/grande.jar", data, data[..50_000]);
        _server.SupportsRange = false; // responde 200 con el archivo entero aunque se pida un rango

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllBytes(GameFile("mods/grande.jar")).ShouldBe(data);
    }

    [Fact]
    public async Task Identical_files_in_different_paths_do_not_share_a_partial()
    {
        var data = RandomBytes(10_000);
        AddFile("versions/a/natives/glfw.dll", data);
        AddFile("versions/b/natives/glfw.dll", data); // mismo contenido, otra ruta (caso real: natives)
        PublishManifest();

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllBytes(GameFile("versions/a/natives/glfw.dll")).ShouldBe(data);
        File.ReadAllBytes(GameFile("versions/b/natives/glfw.dll")).ShouldBe(data);
    }

    [Fact]
    public async Task Empty_file_is_created()
    {
        AddFile("mods/vacio.txt", Array.Empty<byte>()); // caso real: un .txt vacío dentro de mods/
        AddFile("mods/a.jar", "AAAA");
        PublishManifest();

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        new FileInfo(GameFile("mods/vacio.txt")).Length.ShouldBe(0);
    }

    // ── Extracción en paralelo ───────────────────────────────────────────

    [Fact]
    public async Task Many_blocks_are_downloaded_and_extracted_in_parallel()
    {
        for (var b = 0; b < 8; b++)
            AddBlock($"assets_block_{b:x}.zip",
                Enumerable.Range(0, 20).Select(i => ($"assets/objects/{b:x}{i:x}/{b:x}{i:x}obj", $"bloque {b} archivo {i}")).ToArray());
        PublishManifest();

        var options = Options() with { MaxParallelBlockDownloads = 3, MaxParallelExtractions = 3 };
        var result = await CreateVerifier().VerifyAsync(options, null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.BlocksToRepair.ShouldBe(8);
        for (var b = 0; b < 8; b++)
            for (var i = 0; i < 20; i++)
                File.ReadAllText(GameFile($"assets/objects/{b:x}{i:x}/{b:x}{i:x}obj")).ShouldBe($"bloque {b} archivo {i}");
    }

    [Fact]
    public async Task Corrupt_block_fails_without_cleanup()
    {
        AddBlock("bueno.zip", ("assets/objects/aa/aa1", "ok"));
        _blocks["malo.zip"] = new { sha256 = new string('0', 64), size = 3, files = new Dictionary<string, object> { ["assets/objects/bb/bb1"] = new { sha256 = new string('1', 64), size = 1 } } };
        _server.Map("/client/packages/malo.zip", "zip");
        PublishManifest();
        _game.Write("mods/intruso.jar", "x");

        var result = await CreateVerifier().VerifyAsync(Options(), null, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        File.Exists(GameFile("mods/intruso.jar")).ShouldBeTrue(); // con un fallo no se limpia nada
    }

    // ── Parser ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"files\": []}")]
    public void Manifest_without_files_object_is_rejected(string json) =>
        Should.Throw<VerificationException>(() => ManifestParser.Parse(json))
            .Message.ShouldBe("El manifest no contiene una sección files válida");

    [Theory]
    [InlineData("true")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("\"10\"")]
    public void Invalid_sizes_are_rejected(string size) =>
        Should.Throw<VerificationException>(() => ManifestParser.Parse($"{{\"files\":{{\"a\":{{\"sha256\":\"{new string('a', 64)}\",\"size\":{size}}}}}}}"))
            .Message.ShouldStartWith("Tamaño inválido");

    [Fact]
    public void Sha_is_normalized_to_upper_case()
    {
        var manifest = ManifestParser.Parse($"{{\"files\":{{\"mods/a.jar\":{{\"sha256\":\"{new string('a', 64)}\",\"size\":0}}}},\"asset_blocks\":\"x\"}}");
        manifest.Files[0].Sha256.ShouldBe(new string('A', 64));
        manifest.Files[0].RelativePath.ShouldBe(Path.Combine("mods", "a.jar"));
        manifest.AssetBlocks.ShouldBeEmpty(); // no es objeto → lista vacía
    }

    [Fact]
    public void Backslashes_are_normalized()
    {
        ManifestPath.Normalize(@"mods\sub\a.jar").UrlPath.ShouldBe("mods/sub/a.jar");
    }

    /// <summary>IProgress síncrono para capturar estados en orden.</summary>
    private sealed class SyncProgress(Action<LauncherProgress> action) : IProgress<LauncherProgress>
    {
        private readonly object _sync = new();

        public void Report(LauncherProgress value)
        {
            lock (_sync)
                action(value);
        }
    }
}
