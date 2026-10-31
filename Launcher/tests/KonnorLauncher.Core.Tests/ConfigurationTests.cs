using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Pruebas de launcher.properties y de su vista tipada.</summary>
public class ConfigurationTests
{
    private static string OriginalProperties => Path.Combine(AppContext.BaseDirectory, "TestData", "launcher.properties");

    [Fact]
    public void Loads_original_python_properties()
    {
        var props = new LauncherProperties(OriginalProperties);

        props.Get("server.url").ShouldBe("http://127.0.0.1:8080");
        props.Get("versions.path").ShouldBe("versions");
        props.Get("enable.verify").ShouldBe("false");
        props.Get("game.path").ShouldBe(string.Empty);
        props.Contains("player.uuid").ShouldBeFalse();
    }

    [Fact]
    public void Set_rewrites_only_the_key_line_and_keeps_comments_and_order()
    {
        using var dir = new TempDir();
        var path = dir.Combine("launcher.properties");
        File.Copy(OriginalProperties, path);
        var before = File.ReadAllLines(path);

        var props = new LauncherProperties(path);
        props.Set("minecraft.version", "1.21.1");

        var after = File.ReadAllLines(path);
        after.Length.ShouldBe(before.Length);
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].StartsWith("minecraft.version=", StringComparison.Ordinal))
                after[i].ShouldBe("minecraft.version=1.21.1");
            else
                after[i].ShouldBe(before[i]); // comentarios, líneas vacías y orden intactos
        }

        // Se conservan los saltos de línea CRLF del archivo original.
        File.ReadAllText(path).ShouldContain("\r\n");
        new LauncherProperties(path).Get("minecraft.version").ShouldBe("1.21.1");
    }

    [Fact]
    public void Set_appends_missing_key_at_the_end()
    {
        using var dir = new TempDir();
        var path = dir.Combine("launcher.properties");
        File.WriteAllText(path, "# comentario\na=1"); // sin salto final

        new LauncherProperties(path).Set("b", "2");

        File.ReadAllText(path).ShouldBe("# comentario\na=1\nb=2\n");
    }

    [Fact]
    public void Missing_file_is_empty_and_is_created_on_save()
    {
        using var dir = new TempDir();
        var path = dir.Combine("sub", "launcher.properties");
        var props = new LauncherProperties(path);
        props.Get("x", "def").ShouldBe("def");

        props.Set("x", "1");

        File.Exists(path).ShouldBeTrue();
        File.Exists(path + ".tmp").ShouldBeFalse(); // escritura atómica sin restos
    }

    [Fact]
    public void Parsing_is_tolerant_like_python()
    {
        using var dir = new TempDir();
        var path = dir.Write("p.properties", "\uFEFFgame.path = C:\\juego \n  # k=v\nsin igual\nurl=http://a=b\n");
        var props = new LauncherProperties(path);

        props.Get("game.path").ShouldBe("C:\\juego"); // BOM descartado y espacios recortados
        props.Contains("# k").ShouldBeFalse();
        props.Get("url").ShouldBe("http://a=b");     // solo se divide en el primer '='
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" True ", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseBool_only_accepts_literal_true(string? value, bool expected) =>
        LauncherSettings.ParseBool(value).ShouldBe(expected);

    [Theory]
    [InlineData("4G", 4096)]
    [InlineData("4g", 4096)]
    [InlineData("16G", 16384)]
    [InlineData("512M", 512)]
    [InlineData("2048", 2048)]
    [InlineData("4 GB", 4096)]
    [InlineData("abc", null)]
    [InlineData("0G", null)]
    [InlineData("", null)]
    public void ParseMemoryMb_converts_xmx_format(string value, int? expected) =>
        LauncherSettings.ParseMemoryMb(value).ShouldBe(expected);

    [Theory]
    [InlineData(4096, "4G")]
    [InlineData(1024, "1G")]
    [InlineData(1536, "1536M")]
    public void FormatMemory_round_trips(int mb, string expected) =>
        LauncherSettings.FormatMemory(mb).ShouldBe(expected);

    [Fact]
    public void Settings_defaults_match_python()
    {
        using var dir = new TempDir();
        var settings = new LauncherSettings(new LauncherProperties(dir.Combine("none.properties")));

        settings.MemoryMb.ShouldBe(4096);
        settings.VersionsPath.ShouldBe("versions");
        settings.EnableVerify.ShouldBeFalse();
        settings.DeleteUnauthorized.ShouldBeFalse(); // si falta la clave, la limpieza queda desactivada
        settings.PlayerUuid.ShouldBeNull();
        settings.PlayerAccessToken.ShouldBeNull();
    }
}
