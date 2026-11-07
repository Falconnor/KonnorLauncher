using KonnorLauncher.Core.Launching;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Players;
using KonnorLauncher.Core.Tests.Infrastructure;
using KonnorLauncher.Core.Versions;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Rutas, nombre de jugador, UUID offline, Java, versiones y skins.</summary>
public class PathsAndPlayersTests
{
    // ── LauncherPaths ────────────────────────────────────────────────────

    [Fact]
    public void Empty_game_path_resolves_to_official_minecraft()
    {
        var paths = new LauncherPaths(@"C:\Launcher");
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

        paths.ResolveGameDir("").ShouldBe(expected);
        paths.ResolveGameDir("   ").ShouldBe(expected);
        paths.ResolveGameDir(null).ShouldBe(expected);
        LauncherPaths.IsOfficialMinecraftDir(expected + "\\").ShouldBeTrue();
    }

    [Fact]
    public void Relative_game_path_is_relative_to_launcher_dir()
    {
        var paths = new LauncherPaths(@"C:\Launcher\bin");
        paths.ResolveGameDir("game").ShouldBe(@"C:\Launcher\bin\game");
        paths.ResolveGameDir(@"..\Minecraft").ShouldBe(@"C:\Launcher\Minecraft");
        paths.ResolveGameDir(@"D:\Juegos\MC").ShouldBe(@"D:\Juegos\MC");
    }

    [Fact]
    public void Game_sub_dirs_are_relative_to_game_dir_or_absolute()
    {
        LauncherPaths.ResolveGameSubDir(@"C:\mc", "versions", "versions").ShouldBe(@"C:\mc\versions");
        LauncherPaths.ResolveGameSubDir(@"C:\mc", "", "versions").ShouldBe(@"C:\mc\versions");
        LauncherPaths.ResolveGameSubDir(@"C:\mc", @"E:\v", "versions").ShouldBe(@"E:\v");
    }

    // ── UsernameRules ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Steve", true)]
    [InlineData("abc_123", true)]
    [InlineData("abcd", true)]
    [InlineData("abcdefghijklmnop", true)]
    [InlineData("abc", false)]
    [InlineData("abcdefghijklmnopq", false)]
    [InlineData("con espacio", false)]
    [InlineData("ñandú", false)]
    [InlineData("", false)]
    public void Username_regex(string name, bool valid) => UsernameRules.IsValid(name).ShouldBe(valid);

    [Fact]
    public void Options_messages_are_identical_to_python()
    {
        UsernameRules.ValidateForOptions("").ShouldBe("el nombre no puede estar vacío");
        UsernameRules.ValidateForOptions("abc").ShouldBe("el nombre debe contener mínimo 4 caracteres");
        UsernameRules.ValidateForOptions(new string('a', 17)).ShouldBe("el nombre no puede superar 16 caracteres");
        UsernameRules.ValidateForOptions("a-b-c").ShouldBe("solo letras, números y guion bajo ( _ )");
        UsernameRules.ValidateForOptions("Steve").ShouldBeNull();
    }

    [Fact]
    public void Prompt_messages_are_identical_to_python()
    {
        UsernameRules.ValidateForPrompt(" ").ShouldBe("El nombre no puede estar vacío");
        UsernameRules.ValidateForPrompt("ab").ShouldBe("Mínimo 4 caracteres");
        UsernameRules.ValidateForPrompt("a b c").ShouldBe("Solo letras, números y guiones bajos (_)");
        UsernameRules.ValidateForPrompt("Steve").ShouldBeNull();
    }

    // ── OfflineUuid ──────────────────────────────────────────────────────

    [Fact]
    public void Offline_uuid_matches_java_nameUUIDFromBytes()
    {
        // UUID.nameUUIDFromBytes("OfflinePlayer:Steve".getBytes(UTF_8))
        var uuid = OfflineUuid.FromUsername("Steve");
        uuid.ShouldBe("5627dd98e6be3c21b8a8e92344183641");
        uuid[12].ShouldBe('3'); // versión 3
    }

    [Fact]
    public void Session_respects_manual_uuid_and_token()
    {
        var auto = GameLauncher.CreateSession(new LaunchRequest { GameDir = "C:\\mc", VersionId = "1.21", Username = "Steve" });
        auto.UUID.ShouldBe("5627dd98e6be3c21b8a8e92344183641");
        auto.AccessToken.ShouldBe("0");

        var manual = GameLauncher.CreateSession(new LaunchRequest { GameDir = "C:\\mc", VersionId = "1.21", Username = "Steve", Uuid = "u", AccessToken = "t" });
        manual.UUID.ShouldBe("u");
        manual.AccessToken.ShouldBe("t");
    }

    // ── JavaSelector ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("java")]
    [InlineData("JAVAW.EXE")]
    [InlineData("java.exe")]
    public void Generic_java_values_use_mojang_runtime(string value)
    {
        var selection = JavaSelector.Resolve(value, @"C:\mc");
        selection.OverridePath.ShouldBeNull();
        selection.ConfiguredPathMissing.ShouldBeFalse();
    }

    [Fact]
    public void Existing_relative_java_is_relative_to_game_dir()
    {
        using var dir = new TempDir();
        var java = dir.Write("runtime/bin/java.exe", "x");

        JavaSelector.Resolve(@"runtime\bin\java.exe", dir.Path).OverridePath.ShouldBe(java);
        JavaSelector.Resolve(java, @"C:\otra").OverridePath.ShouldBe(java);
    }

    [Fact]
    public void Missing_java_falls_back_to_mojang_runtime()
    {
        var selection = JavaSelector.Resolve(@"C:\no\existe\java.exe", @"C:\mc");
        selection.OverridePath.ShouldBeNull();
        selection.ConfiguredPathMissing.ShouldBeTrue();
    }

    // ── VersionService ───────────────────────────────────────────────────

    [Fact]
    public void Installed_versions_match_python_rules_and_order()
    {
        using var dir = new TempDir();
        dir.Write("versions/1.21.1/1.21.1.json", "{}");
        dir.Write("versions/fabric-x/otro.json", "{}");  // cualquier .json vale
        dir.Write("versions/Zeta/Zeta.json", "{}");
        dir.Write("versions/vacia/readme.txt", "x");     // sin .json → no cuenta
        File.WriteAllText(dir.Combine("versions", "suelto.json"), "{}"); // archivo, no carpeta

        var installed = VersionService.GetInstalledVersions(dir.Combine("versions"));

        installed.ShouldBe(["1.21.1", "Zeta", "fabric-x"]); // orden ordinal (mayúsculas antes)
    }

    [Fact]
    public void Combined_list_puts_installed_first_without_duplicates()
    {
        var list = VersionService.Combine(["1.20.1", "fabric"], ["1.21.1", "1.20.1", "1.19"]);

        list.Select(v => v.Id).ShouldBe(["1.20.1", "fabric", "1.21.1", "1.19"]);
        list.Count(v => v.IsInstalled).ShouldBe(2);
    }

    [Fact]
    public void Missing_versions_dir_returns_empty() =>
        VersionService.GetInstalledVersions(@"C:\no\existe\versions").ShouldBeEmpty();

    // ── SkinComposer ─────────────────────────────────────────────────────

    [Fact]
    public void Classic_skin_mirrors_right_limbs()
    {
        // Skin clásica 64x32: brazo izquierdo (44,20) con su columna izquierda roja.
        var skin = new byte[64 * 32 * 4];
        for (var y = 20; y < 32; y++)
            SetPixel(skin, 64, 44, y, 0, 0, 255, 255);

        var front = SkinComposer.ComposeFront(new SkinComposer.Pixels(64, 32, skin));

        // Brazo izquierdo en pantalla (destino x=0): columna 0 roja.
        GetPixel(front.Bgra, 16, 0, 8)[2].ShouldBe((byte)255);
        // Brazo derecho espejado (destino x=12..15): la columna roja pasa a x=15.
        GetPixel(front.Bgra, 16, 15, 8)[2].ShouldBe((byte)255);
        GetPixel(front.Bgra, 16, 12, 8)[3].ShouldBe((byte)0);
    }

    [Fact]
    public void Hat_layer_is_drawn_over_the_head()
    {
        var skin = new byte[64 * 64 * 4];
        SetPixel(skin, 64, 8, 8, 255, 0, 0, 255);  // cabeza: azul
        SetPixel(skin, 64, 40, 8, 0, 255, 0, 255); // sombrero: verde, mismo punto
        var front = SkinComposer.ComposeFront(new SkinComposer.Pixels(64, 64, skin));

        GetPixel(front.Bgra, 16, 4, 0).ShouldBe(new byte[] { 0, 255, 0, 255 });
    }

    private static void SetPixel(byte[] data, int width, int x, int y, byte b, byte g, byte r, byte a)
    {
        var i = (y * width + x) * 4;
        data[i] = b;
        data[i + 1] = g;
        data[i + 2] = r;
        data[i + 3] = a;
    }

    private static byte[] GetPixel(byte[] data, int width, int x, int y) =>
        data.AsSpan((y * width + x) * 4, 4).ToArray();
}
