using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Tests.Infrastructure;
using KonnorLauncher.Core.Theming;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Temas (Opciones → Tema): paleta generada a partir del acento y claves de launcher.properties.</summary>
public class ThemeTests
{
    [Theory]
    [InlineData("#3478F6", "#3478F6")]
    [InlineData("3478f6", "#3478F6")]
    [InlineData("#263478F6", "#263478F6")]
    public void Colors_are_parsed_and_printed(string text, string expected)
    {
        Argb.TryParse(text, out var color).ShouldBeTrue();
        color.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("rojo")]
    [InlineData(null)]
    public void Invalid_colors_are_rejected(string? text) => Argb.TryParse(text, out _).ShouldBeFalse();

    [Theory]
    [InlineData("#3478F6")]
    [InlineData("#27AE60")]
    [InlineData("#FF9500")]
    [InlineData("#8E8E93")]
    public void Hsl_round_trips(string hex)
    {
        var color = Argb.Parse(hex);
        var (h, s, l) = color.ToHsl();
        Argb.FromHsl(h, s, l).ShouldBe(color);
    }

    [Fact]
    public void The_default_theme_is_the_original_design()
    {
        var palette = ThemeEngine.Build(ThemeSettings.Default);

        palette["Accent"].ToString().ShouldBe("#3478F6");
        palette["Brand"].ToString().ShouldBe("#27AE60");      // JUGAR sigue verde
        palette["WindowTint"].ToString().ShouldBe("#B81B1F2C");
        palette["Ink"].ToString().ShouldBe("#F3F5F7");
        palette["AuroraBlue"].ToString().ShouldBe("#3A5BFF");
        palette["AuroraBlueEnd"].A.ShouldBe((byte)0);
        palette["OnAccent"].ToString().ShouldBe("#FFFFFF");
        Close(palette["AccentHover"], Argb.Parse("#4A88FF")).ShouldBeTrue();
        Close(palette["AccentPress"], Argb.Parse("#2563D9")).ShouldBeTrue();
        Close(palette["BrandMid"], Argb.Parse("#34C472")).ShouldBeTrue();
        Close(palette["BrandDark"], Argb.Parse("#219A54")).ShouldBeTrue();
    }

    [Fact]
    public void Every_preset_builds_every_key()
    {
        foreach (var preset in ThemeEngine.Presets)
        {
            var palette = ThemeEngine.Build(new ThemeSettings(preset.Id));
            palette.Keys.Order().ShouldBe(ThemeEngine.Keys.Order(), preset.Id);
            palette["Accent"].ToString().ShouldBe(preset.Accent, preset.Id);
        }

        ThemeEngine.Presets.Select(p => p.Id).ShouldBeUnique();
    }

    [Fact]
    public void Glass_and_aurora_take_the_hue_of_the_accent()
    {
        var violet = ThemeEngine.Build(new ThemeSettings("violeta"));
        var accentHue = violet["Accent"].ToHsl().H;

        // El cristal y la aurora se separan del azul lo mismo que el acento: conservan su relación con él.
        var original = ThemeEngine.Build(ThemeSettings.Default);
        var delta = accentHue - original["Accent"].ToHsl().H;
        Hue(violet["WindowTint"]).ShouldBe(Norm(Hue(original["WindowTint"]) + delta), 2);
        Hue(violet["AuroraBlue"]).ShouldBe(Norm(Hue(original["AuroraBlue"]) + delta), 2);
        violet["WindowTint"].A.ShouldBe(original["WindowTint"].A);  // la opacidad no cambia
        violet["Brand"].ShouldBe(violet["Accent"]);                // JUGAR del mismo color
    }

    [Fact]
    public void A_grey_accent_gives_a_neutral_glass()
    {
        var graphite = ThemeEngine.Build(new ThemeSettings("grafito"));
        graphite["WindowTint"].ToHsl().S.ShouldBeLessThan(ThemeEngine.Build(ThemeSettings.Default)["WindowTint"].ToHsl().S / 3);

        // Un acento propio gris tampoco tiñe nada de colores raros.
        var custom = ThemeEngine.Build(new ThemeSettings("violeta", CustomAccent: "#808080"));
        custom["WindowTint"].ToHsl().S.ShouldBeLessThan(0.05);
    }

    [Theory]
    [InlineData("#3478F6", "#FFFFFF")]
    [InlineData("#9146FF", "#FFFFFF")]
    [InlineData("#FF9500", "#0B0E13")]
    [InlineData("#1DB954", "#0B0E13")]
    [InlineData("#FFFFFF", "#0B0E13")]
    public void Text_on_the_accent_stays_readable(string accent, string expected)
    {
        var text = ThemeEngine.TextOn(Argb.Parse(accent));
        text.ToString().ShouldBe(expected);
        Argb.Contrast(text, Argb.Parse(accent)).ShouldBeGreaterThanOrEqualTo(3.0);
    }

    [Fact]
    public void A_custom_accent_wins_over_the_preset_and_also_paints_play()
    {
        var palette = ThemeEngine.Build(new ThemeSettings(ThemeEngine.DefaultPresetId, CustomAccent: "#E91E63"));
        palette["Accent"].ToString().ShouldBe("#E91E63");
        palette["Brand"].ToString().ShouldBe("#E91E63");
    }

    [Theory]
    [InlineData(100, 0xB8)]
    [InlineData(60, 0x6E)]
    [InlineData(20, 0x6E)]   // por debajo del mínimo: el mínimo
    [InlineData(140, 0xFF)]
    public void Glass_opacity_scales_only_the_glass(int percent, int expectedAlpha)
    {
        var palette = ThemeEngine.Build(new ThemeSettings(ThemeEngine.DefaultPresetId, GlassPercent: percent));
        palette["WindowTint"].A.ShouldBe((byte)expectedAlpha);
        palette["Ink"].A.ShouldBe((byte)0xFF);
        palette["Accent"].A.ShouldBe((byte)0xFF);
    }

    [Fact]
    public void Theme_is_read_from_launcher_properties()
    {
        using var dir = new TempDir();
        var path = dir.Combine("launcher.properties");
        File.WriteAllText(path, "theme.preset=Violeta\ntheme.accent=e91e63\ntheme.glass=85\ntheme.background=C:/fondo.png\ntheme.mode=Solid\n");

        var theme = new LauncherSettings(new LauncherProperties(path)).Theme;

        theme.ShouldBe(new ThemeSettings("violeta", "#E91E63", 85, "C:/fondo.png", Solid: true));
    }

    [Theory]
    [InlineData(ThemeEngine.DefaultPresetId)]
    [InlineData("violeta")]
    [InlineData("grafito")]
    public void A_solid_background_is_opaque_and_ignores_the_glass_opacity(string preset)
    {
        var solid = ThemeEngine.Build(new ThemeSettings(preset, GlassPercent: 60, Solid: true));

        foreach (var key in new[] { "WindowTint", "GlassTint", "SidebarTint", "DialogTint", "MenuGlass" })
            solid[key].A.ShouldBe((byte)0xFF, key);

        // Oscuro y casi neutro, como los temas oscuros de los editores: se lee la tinta encima.
        solid["WindowTint"].ToHsl().L.ShouldBeLessThan(0.15);
        Argb.Contrast(solid["Ink"], solid["WindowTint"]).ShouldBeGreaterThan(12);
        // Escalones: barra lateral más oscura que la ventana; diálogos más claros.
        solid["SidebarTint"].Luminance().ShouldBeLessThan(solid["WindowTint"].Luminance());
        solid["DialogTint"].Luminance().ShouldBeGreaterThan(solid["WindowTint"].Luminance());
        // El acento no cambia por el tipo de fondo.
        solid["Accent"].ShouldBe(ThemeEngine.Build(new ThemeSettings(preset))["Accent"]);
    }

    [Fact]
    public void A_solid_background_keeps_the_hue_of_the_theme()
    {
        var violet = ThemeEngine.Build(new ThemeSettings("violeta", Solid: true));
        var blue = ThemeEngine.Build(new ThemeSettings(ThemeEngine.DefaultPresetId, Solid: true));
        Math.Abs(Hue(violet["WindowTint"]) - Hue(blue["WindowTint"])).ShouldBeGreaterThan(30);
    }

    [Fact]
    public void Missing_or_invalid_theme_keys_fall_back_to_the_design()
    {
        using var dir = new TempDir();
        var path = dir.Combine("launcher.properties");
        File.WriteAllText(path, "theme.preset=inventado\ntheme.accent=azulito\ntheme.glass=mucho\n");

        new LauncherSettings(new LauncherProperties(path)).Theme.ShouldBe(ThemeSettings.Default);
    }

    private static double Hue(Argb color) => color.ToHsl().H;

    private static double Norm(double hue) => ((hue % 360) + 360) % 360;

    /// <summary>Iguales salvo redondeo (las variantes se calculan, no se copian).</summary>
    private static bool Close(Argb a, Argb b) =>
        Math.Abs(a.R - b.R) <= 6 && Math.Abs(a.G - b.G) <= 6 && Math.Abs(a.B - b.B) <= 6;
}
