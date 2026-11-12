using CmlLib.Core.ProcessBuilder;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Launching;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Opciones del jugador (sección Opciones): argumentos de Java, ventana del juego y comportamiento del launcher.</summary>
public class PlayerOptionsTests
{
    private static LauncherSettings Settings(TempDir dir, string content)
    {
        var path = dir.Combine("launcher.properties");
        File.WriteAllText(path, content);
        return new LauncherSettings(new LauncherProperties(path));
    }

    [Fact]
    public void Missing_keys_keep_minecraft_defaults()
    {
        using var dir = new TempDir();
        var settings = Settings(dir, "java.memory=4G\n");

        settings.JavaArgs.ShouldBeEmpty();
        settings.GameWidth.ShouldBeNull();
        settings.GameHeight.ShouldBeNull();
        settings.GameFullscreen.ShouldBeFalse();
        settings.OnPlay.ShouldBe(LauncherOnPlay.Keep);
        settings.OpenLogOnCrash.ShouldBeFalse();
    }

    [Fact]
    public void Player_options_are_read_from_the_file()
    {
        using var dir = new TempDir();
        var settings = Settings(dir, """
            java.args= -XX:+UseG1GC
            game.width=1920
            game.height=1080
            game.fullscreen=true
            launcher.on_play=Hide
            launcher.open_log_on_crash=true
            """);

        settings.JavaArgs.ShouldBe("-XX:+UseG1GC");
        settings.GameWidth.ShouldBe(1920);
        settings.GameHeight.ShouldBe(1080);
        settings.GameFullscreen.ShouldBeTrue();
        settings.OnPlay.ShouldBe(LauncherOnPlay.Hide);
        settings.OpenLogOnCrash.ShouldBeTrue();
    }

    [Theory]
    [InlineData("1280", 1280)]
    [InlineData(" 720 ", 720)]
    [InlineData("319", null)]
    [InlineData("20000", null)]
    [InlineData("-800", null)]
    [InlineData("12.5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void Window_size_must_be_a_sensible_number(string value, int? expected) =>
        LauncherSettings.ParseDimension(value).ShouldBe(expected);

    [Theory]
    [InlineData(LauncherOnPlay.Keep)]
    [InlineData(LauncherOnPlay.Minimize)]
    [InlineData(LauncherOnPlay.Hide)]
    public void On_play_round_trips(LauncherOnPlay value)
    {
        using var dir = new TempDir();
        var settings = Settings(dir, $"launcher.on_play={LauncherSettings.FormatOnPlay(value)}\n");
        settings.OnPlay.ShouldBe(value);
    }

    [Fact]
    public void Unknown_on_play_keeps_the_launcher_open()
    {
        using var dir = new TempDir();
        Settings(dir, "launcher.on_play=close\n").OnPlay.ShouldBe(LauncherOnPlay.Keep);
    }

    [Fact]
    public void Arguments_are_split_respecting_quotes()
    {
        GameLauncher.SplitArguments("""  -XX:+UseG1GC   -Dfoo="a b"  "-Dbar=c d" """)
            .ShouldBe(["-XX:+UseG1GC", "-Dfoo=a b", "-Dbar=c d"]);
        GameLauncher.SplitArguments(null).ShouldBeEmpty();
        GameLauncher.SplitArguments("   ").ShouldBeEmpty();
    }

    [Fact]
    public void Launch_options_get_the_window_and_the_extra_arguments_but_not_the_memory()
    {
        var option = new MLaunchOption();
        GameLauncher.ApplyPlayerOptions(option, Request() with
        {
            JvmArguments = "-Xmx16G -XX:+UseG1GC -Xms8G",
            ScreenWidth = 1600,
            ScreenHeight = 900,
            FullScreen = true,
        });

        option.ScreenWidth.ShouldBe(1600);
        option.ScreenHeight.ShouldBe(900);
        option.FullScreen.ShouldBeTrue();
        var values = option.ExtraJvmArguments.SelectMany(a => a.Values ?? []).ToList();
        values.ShouldContain("-Dlog4j2.formatMsgNoLookups=true"); // los de CmlLib se conservan
        values[^1].ShouldBe("-XX:+UseG1GC");
        values.ShouldNotContain(v => v.StartsWith("-Xm"));         // la memoria la decide el launcher
    }

    [Fact]
    public void Without_player_options_minecraft_keeps_its_defaults()
    {
        var option = new MLaunchOption();
        GameLauncher.ApplyPlayerOptions(option, Request() with { ScreenWidth = 1600, JvmArguments = "  " }); // solo el ancho: no cuenta

        option.ScreenWidth.ShouldBe(0);
        option.ScreenHeight.ShouldBe(0);
        option.FullScreen.ShouldBeFalse();
        option.ExtraJvmArguments.ShouldBe(new MLaunchOption().ExtraJvmArguments); // los de CmlLib, sin tocar
    }

    private static LaunchRequest Request() => new() { GameDir = "game", VersionId = "1.21.1", Username = "Steve" };
}
