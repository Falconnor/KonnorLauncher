using System.Globalization;

namespace KonnorLauncher.Core.Theming;

/// <summary>Color ARGB de 8 bits por canal (sin depender de WPF, para poder probarlo en el Core).</summary>
public readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    public static Argb Rgb(byte r, byte g, byte b) => new(0xFF, r, g, b);

    /// <summary>Lee <c>#RRGGBB</c> o <c>#AARRGGBB</c> (con o sin <c>#</c>).</summary>
    public static bool TryParse(string? text, out Argb color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#') ?? string.Empty;
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;
        if (hex.Length == 6)
            value |= 0xFF000000;
        color = new Argb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static Argb Parse(string text) =>
        TryParse(text, out var color) ? color : throw new FormatException($"Color no válido: {text}");

    public Argb WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary><c>#RRGGBB</c> si es opaco; si no, <c>#AARRGGBB</c>.</summary>
    public override string ToString() => A == 0xFF ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    // ── HSL ──────────────────────────────────────────────────────────────

    /// <summary>Tono (0-360), saturación y luminosidad (0-1).</summary>
    public (double H, double S, double L) ToHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9)
            return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r)
            h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g)
            h = (b - r) / d + 2;
        else
            h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Argb FromHsl(double h, double s, double l, byte alpha = 0xFF)
    {
        h = ((h % 360) + 360) % 360 / 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s < 1e-9)
        {
            var gray = ToByte(l);
            return new Argb(alpha, gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return new Argb(alpha, ToByte(Hue(p, q, h + 1.0 / 3)), ToByte(Hue(p, q, h)), ToByte(Hue(p, q, h - 1.0 / 3)));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }

    // ── HSV (rueda de color: ángulo = tono, distancia al centro = saturación) ──

    /// <summary>Tono (0-360), saturación y brillo (0-1).</summary>
    public (double H, double S, double V) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        var s = max <= 0 ? 0 : d / max;
        return (ToHsl().H, s, max);
    }

    public static Argb FromHsv(double h, double s, double v, byte alpha = 0xFF)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new Argb(alpha, ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    /// <summary>Mismo color con la luminosidad y la saturación desplazadas (en puntos de 0 a 1).</summary>
    public Argb Shift(double lightness, double saturation = 0)
    {
        var (h, s, l) = ToHsl();
        return FromHsl(h, s + saturation, l + lightness, A);
    }

    // ── Contraste (WCAG 2) ───────────────────────────────────────────────

    public double Luminance()
    {
        static double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
    }

    public static double Contrast(Argb a, Argb b)
    {
        var (la, lb) = (a.Luminance(), b.Luminance());
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}

/// <summary>
/// Tema de serie. <paramref name="Brand"/> es el color de JUGAR y de las barras de progreso (null = el acento).
/// <paramref name="TintSaturation"/> escala la saturación del cristal y de la aurora (1 = como el tema por defecto).
/// <paramref name="TintLift"/> aclara las superficies (ventana, barra lateral, diálogos…), en puntos de luminosidad de
/// 0 a 1: los temas pastel quedan apagados si el cristal es tan oscuro como el del azul.
/// </summary>
public sealed record ThemePreset(string Id, string Name, string Accent, string? Brand = null, double TintSaturation = 1.0, double TintLift = 0);

/// <summary>Tema elegido por el jugador (claves <c>theme.*</c> de <c>launcher.properties</c>).</summary>
/// <param name="PresetId">Tema de serie.</param>
/// <param name="CustomAccent">Color de acento propio (<c>#RRGGBB</c>); null = el del tema.</param>
/// <param name="GlassPercent">Opacidad del cristal: 100 = la del diseño.</param>
/// <param name="BackgroundPath">Imagen de fondo propia; null = la del launcher.</param>
/// <param name="Solid">
/// Fondo sólido (como VS Code o Modrinth): superficies opacas, sin imagen, aurora ni desenfoque. false = cristal
/// translúcido sobre la imagen de fondo.
/// </param>
public sealed record ThemeSettings(string PresetId, string? CustomAccent = null, int GlassPercent = 100, string? BackgroundPath = null, bool Solid = false)
{
    public static ThemeSettings Default { get; } = new(ThemeEngine.DefaultPresetId);
}

/// <summary>
/// Genera la paleta completa de un tema a partir de su acento. Los colores neutros del diseño (cristal, tinta,
/// fondo) y la aurora tienen un ligero tinte azul pizarra: se giran en el círculo cromático lo mismo que el acento
/// se separa del azul original, así todos los tonos combinan. Los colores con significado propio (correcto, aviso,
/// peligro, cargadores) no cambian. Con el tema por defecto la paleta es la del diseño original.
/// </summary>
public static class ThemeEngine
{
    public const string DefaultPresetId = "azul";
    public const int MinGlassPercent = 60;
    public const int MaxGlassPercent = 140;

    /// <summary>Tono del acento original (#3478F6): los neutros del diseño están pensados para él.</summary>
    private static readonly double BaseHue = Argb.Parse("#3478F6").ToHsl().H;

    /// <summary>Temas de serie: los acentos más habituales en aplicaciones y plataformas.</summary>
    public static IReadOnlyList<ThemePreset> Presets { get; } =
    [
        new(DefaultPresetId, "Azul", "#3478F6", Brand: "#27AE60"),
        new("indigo", "Índigo", "#5865F2"),
        new("violeta", "Violeta", "#A970FF", TintSaturation: 1.5, TintLift: 0.03),
        new("rosa", "Rosa", "#FF4DB8", TintSaturation: 2.4, TintLift: 0.05),
        new("rojo", "Rojo", "#FF3B30"),
        new("naranja", "Naranja", "#FF9A1F", TintSaturation: 2.0, TintLift: 0.04),
        new("verde", "Verde", "#1DB954"),
        new("turquesa", "Turquesa", "#2DD4BF", TintSaturation: 2.0, TintLift: 0.04),
        new("cielo", "Cielo", "#1A9FFF"),
        new("grafito", "Grafito", "#8E8E93", Brand: "#27AE60", TintSaturation: 0.2),
    ];

    /// <summary>Neutros del diseño: se giran con el acento.</summary>
    private static readonly (string Key, string Color)[] Neutrals =
    [
        ("Ink", "#F3F5F7"), ("Ink2", "#BDF3F5F7"), ("Ink3", "#99F3F5F7"),
        ("GlassTint", "#94101418"), ("Scrim", "#59060810"), ("ModalScrim", "#8C04060A"),
        ("WindowTint", "#B81B1F2C"), ("SidebarTint", "#C4151925"), ("TileBackground", "#F20B0E14"),
        ("MenuGlass", "#E61A1E2A"), ("DialogTint", "#D91B1F2C"),
        ("WindowBase", "#0B0E13"), ("FallbackTop", "#0D1C2E"), ("FallbackBottom", "#0A1220"),
    ];

    /// <summary>
    /// Fondo sólido: las mismas capas, opacas y casi neutras (gris azulado muy oscuro, como los editores y las webs
    /// en modo oscuro). La barra lateral es la más oscura; diálogos y menús, un escalón más claros que la ventana.
    /// Las velos de los diálogos oscurecen más, porque detrás ya no hay desenfoque.
    /// </summary>
    private static readonly Dictionary<string, string> SolidNeutrals = new(StringComparer.Ordinal)
    {
        ["WindowTint"] = "#16191F",
        ["GlassTint"] = "#1C1F26",
        ["SidebarTint"] = "#111318",
        ["DialogTint"] = "#1C1F27",
        ["MenuGlass"] = "#1F222A",
        ["ModalScrim"] = "#B3020305",
    };

    /// <summary>
    /// Resaltados (paso del ratón y selección: filas, barra lateral, tarjetas, pastillas). En el diseño son blanco
    /// translúcido; aquí llevan el tono del acento, con la misma opacidad un poco reforzada para que el tinte se note.
    /// Las claves de Explorar y Skins son las de sus vistas.
    /// </summary>
    private static readonly (string Key, byte Alpha)[] Highlights =
    [
        ("RowHover", 0x0F), ("RowSelected", 0x1A), ("InstanceSelected", 0x14), ("SurfaceHover", 0x1A), ("PillGhostHover", 0x26),
        ("SelectedFill", 0x1A), ("SelectedFillStrong", 0x38), ("HoverFillStrong", 0x26), ("TileSelected", 0x24),
    ];

    /// <summary>Superficies que aclara <see cref="ThemePreset.TintLift"/> (la tinta y los velos no).</summary>
    private static readonly HashSet<string> Surfaces =
        ["GlassTint", "WindowTint", "SidebarTint", "TileBackground", "MenuGlass", "DialogTint", "WindowBase", "FallbackTop", "FallbackBottom"];

    /// <summary>Cristal cuya opacidad ajusta el jugador.</summary>
    private static readonly HashSet<string> Glass = ["GlassTint", "WindowTint", "SidebarTint", "MenuGlass", "DialogTint"];

    /// <summary>Manchas de color del fondo: también se giran (y en temas apagados se apagan).</summary>
    private static readonly (string Key, string Color)[] Aurora =
    [
        ("AuroraBlue", "#3A5BFF"), ("AuroraViolet", "#8E3BFF"), ("AuroraRed", "#FF2E63"), ("AuroraCyan", "#1FB6FF"),
    ];

    private static readonly Argb White = Argb.Rgb(0xFF, 0xFF, 0xFF);
    private static readonly Argb DarkText = Argb.Rgb(0x0B, 0x0E, 0x13);

    /// <summary>Todas las claves que genera <see cref="Build"/>.</summary>
    public static IReadOnlyList<string> Keys { get; } = Build(ThemeSettings.Default).Keys.ToList();

    public static ThemePreset FindPreset(string? id) =>
        Presets.FirstOrDefault(p => string.Equals(p.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

    /// <summary>Brillo mínimo de un color propio: más oscuro no se distinguiría sobre el cristal.</summary>
    public const double MinCustomBrightness = 0.35;

    /// <summary>Acento efectivo: el propio si es válido; si no, el del tema.</summary>
    public static Argb AccentOf(ThemeSettings settings) =>
        Argb.TryParse(settings.CustomAccent, out var custom) ? custom with { A = 0xFF } : Argb.Parse(FindPreset(settings.PresetId).Accent);

    public static IReadOnlyDictionary<string, Argb> Build(ThemeSettings settings)
    {
        var preset = FindPreset(settings.PresetId);
        var hasCustom = Argb.TryParse(settings.CustomAccent, out _);
        var accent = AccentOf(settings);
        // Un acento propio también pinta JUGAR; el tema de serie puede fijar otro (el verde del launcher).
        var brand = !hasCustom && preset.Brand is { } presetBrand ? Argb.Parse(presetBrand) : accent;
        var (accentHue, accentSat, _) = accent.ToHsl();
        // Un acento casi gris no tiene tono: se conserva el del diseño y se apaga el tinte.
        var tint = hasCustom ? Math.Clamp(accentSat / 0.5, 0, 1) : preset.TintSaturation;
        var rotation = accentSat < 0.08 ? 0 : accentHue - BaseHue;
        var glass = Math.Clamp(settings.GlassPercent, MinGlassPercent, MaxGlassPercent) / 100.0;

        var palette = new Dictionary<string, Argb>(StringComparer.Ordinal);
        foreach (var (key, neutral) in Neutrals)
        {
            var hex = settings.Solid && SolidNeutrals.TryGetValue(key, out var solid) ? solid : neutral;
            var color = Rotate(Argb.Parse(hex), rotation, tint);
            if (!hasCustom && preset.TintLift > 0 && Surfaces.Contains(key))
                color = color.Shift(preset.TintLift);
            if (Glass.Contains(key) && !settings.Solid)
                color = color.WithAlpha((byte)Math.Clamp(Math.Round(color.A * glass), 0, 255));
            palette[key] = color;
        }

        foreach (var (key, hex) in Aurora)
        {
            var color = Rotate(Argb.Parse(hex), rotation, Math.Max(tint, 0.15));
            palette[key] = color;
            palette[key + "End"] = color.WithAlpha(0);
        }

        // Acento: las variantes mantienen la relación del diseño original (#3478F6 → #4A88FF / #2563D9).
        palette["Accent"] = accent;
        palette["AccentHover"] = accent.Shift(0.07, 0.08);
        palette["AccentPress"] = accent.Shift(-0.08, -0.21);
        palette["AccentSoft"] = accent.WithAlpha(0x26);
        palette["AccentGlow"] = accent.WithAlpha(0x33);
        palette["OnAccent"] = TextOn(accent);

        // Resaltados: blanco teñido con el acento (mitad y mitad), así combinan con la selección y los botones.
        var highlight = Mix(White, accent, 0.5);
        foreach (var (key, alpha) in Highlights)
            palette[key] = highlight.WithAlpha((byte)Math.Min(255, Math.Round(alpha * 1.35)));

        // Marca (JUGAR y progreso): mismas relaciones que el verde original (#27AE60).
        palette["Brand"] = brand;
        palette["BrandPress"] = brand.Shift(-0.10);
        palette["BrandLight"] = brand.Shift(0.12);
        palette["BrandMid"] = brand.Shift(0.07, -0.05);
        palette["BrandDark"] = brand.Shift(-0.05);
        palette["BrandSoft"] = brand.WithAlpha(0x4D);
        palette["OnBrand"] = TextOn(brand);
        return palette;
    }

    /// <summary>Texto sobre un color: blanco mientras se lea (contraste ≥ 3, como los botones del diseño); si no, oscuro.</summary>
    public static Argb TextOn(Argb background) => Argb.Contrast(White, background) >= 3.0 ? White : DarkText;

    /// <summary>Mezcla de dos colores opacos: 0 = <paramref name="a"/>, 1 = <paramref name="b"/>.</summary>
    private static Argb Mix(Argb a, Argb b, double t) => Argb.Rgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Argb Rotate(Argb color, double degrees, double saturation)
    {
        if (Math.Abs(degrees) < 1e-9 && Math.Abs(saturation - 1) < 1e-9)
            return color; // tema por defecto: exactamente el diseño original
        var (h, s, l) = color.ToHsl();
        return Argb.FromHsl(h + degrees, s * saturation, l, color.A);
    }
}
