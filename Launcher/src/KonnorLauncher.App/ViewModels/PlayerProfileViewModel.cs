using System.Globalization;
using System.Windows.Media;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Hosting;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Jugador de la lista: nombre, conectado y su estado guardado (null si aún no hay datos).</summary>
public sealed record PlayerEntry(string Name, bool IsOnline, PlayerProfile? Profile = null)
{
    public bool HasProfile => Profile is not null;
    public string HealthText => Profile is { } p ? PlayerProfileViewModel.Half(p.Health) : string.Empty;
    public string FoodText => Profile is { } p ? p.Food.ToString(CultureInfo.InvariantCulture) : string.Empty;
    public string ArmorText => Profile is { } p ? p.Armor.ToString(CultureInfo.InvariantCulture) : string.Empty;
    public string LevelText => Profile is { } p ? $"Nivel {p.XpLevel}" : string.Empty;
    public string GameModeText => Profile?.GameModeName ?? string.Empty;

    // Iconos del juego para el resumen (si no hay, se escriben las palabras).
    public bool ShowSprites => HudSprites.Available;
    public bool ShowWords => !HudSprites.Available;
    public ImageSource? HeartIcon => HudSprites.Get("heart/full.png");
    public ImageSource? FoodIcon => HudSprites.Get("food_full.png");
    public ImageSource? ArmorIcon => HudSprites.Get("armor_full.png");
}

/// <summary>Un icono del HUD: el fondo (contenedor vacío) y lo que se dibuja encima (lleno, medio o nada).</summary>
public sealed record HudIcon(ImageSource? Back, ImageSource? Front);

/// <summary>Fila de iconos del perfil: corazones, muslos, armadura o burbujas, con su valor escrito.</summary>
public sealed record HudRow(string Label, IReadOnlyList<HudIcon> Icons, string Value);

/// <summary>Fila «etiqueta: valor» del perfil.</summary>
public sealed record ProfileFact(string Label, string Value);

/// <summary>Barra del perfil (vida, hambre, armadura, experiencia, aire).</summary>
public sealed record ProfileBar(string Label, string Value, double Ratio, string Color);

/// <summary>Perfil de un jugador en el mundo del servidor, con los textos ya formateados.</summary>
public sealed class PlayerProfileViewModel
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private static readonly (string Slot, string Label)[] EquipmentSlots =
        [("head", "Casco"), ("chest", "Peto"), ("legs", "Pantalones"), ("feet", "Botas"), ("offhand", "Mano secundaria"), ("body", "Cuerpo")];

    public PlayerProfileViewModel(PlayerProfile profile, bool isOnline)
    {
        Profile = profile;
        IsOnline = isOnline;
        Initial = profile.Name[..1].ToUpperInvariant();

        Bars =
        [
            new("Vida", $"{Half(profile.Health)} / {Half(profile.MaxHealth)}" + (profile.Absorption > 0 ? $" (+{Half(profile.Absorption)})" : string.Empty),
                Ratio(profile.Health, profile.MaxHealth), "#E5484D"),
            new("Hambre", $"{profile.Food} / 20 · saturación {Half(profile.Saturation)}", Ratio(profile.Food, 20), "#E2A336"),
            new("Armadura", $"{profile.Armor} / 20", Ratio(profile.Armor, 20), "#9BA8B8"),
            new("Experiencia", $"Nivel {profile.XpLevel} · {profile.XpProgress:P0} del siguiente · {profile.XpTotal} puntos en total", profile.XpProgress, "#7BD45C"),
            new("Aire", profile.Air >= 300 ? "Lleno" : $"{Math.Max(0, profile.Air) / 20} s", Ratio(profile.Air, 300), "#5AA9E6"),
        ];

        State =
        [
            new("Modo de juego", profile.GameModeName),
            new("Estado", string.Join(" · ", new[]
            {
                profile.Health <= 0 ? "muerto" : null,
                profile.OnFire ? "en llamas" : null,
                profile.Flying ? "volando" : null,
                profile.CanFly && !profile.Flying ? "puede volar" : null,
                profile.Invulnerable ? "invulnerable" : null,
            }.Where(t => t is not null).DefaultIfEmpty("normal"))),
            new("Puntuación", profile.Score.ToString(Es)),
            new("UUID", profile.Uuid.ToString()),
        ];

        Location =
        [
            new("Posición", profile.Position is { } pos ? $"{pos.Coordinates} · {pos.DimensionName}" : "Desconocida"),
            new("Punto de reaparición", profile.Respawn is { } respawn ? $"{respawn.Coordinates} · {respawn.DimensionName} (cama o ancla)"
                : profile.WorldSpawn is { } spawn ? $"{spawn.Coordinates} · {spawn.DimensionName} (spawn del mundo)"
                : "Spawn del mundo"),
            new("Última muerte", profile.LastDeath is { } death ? $"{death.Coordinates} · {death.DimensionName}" : "Nunca murió"),
        ];

        Equipment = [.. EquipmentSlots
            .Where(s => profile.Equipment.ContainsKey(s.Slot))
            .Select(s => new ProfileFact(s.Label, ItemText(profile.Equipment[s.Slot])))];
        if (profile.HeldItem is { } held)
            Equipment.Insert(0, new ProfileFact("En la mano", ItemText(held)));

        if (HudSprites.Available)
        {
            var maxHearts = (int)Math.Ceiling(Math.Min(profile.MaxHealth, 80) / 2);
            HudRows =
            [
                new("Vida", Points(profile.Health, maxHearts, "heart/container.png", "heart/full.png", "heart/half.png"), Bars[0].Value),
                new("Hambre", Points(profile.Food, 10, "food_empty.png", "food_full.png", "food_half.png", fromRight: true), Bars[1].Value),
                new("Armadura", Points(profile.Armor, 10, "armor_empty.png", "armor_full.png", "armor_half.png", standalone: true), Bars[2].Value),
                new("Aire", Bubbles(profile.Air), Bars[4].Value),
            ];
            if (profile.Absorption > 0)
                HudRows.Insert(1, new("Absorción", Points(profile.Absorption, (int)Math.Ceiling(profile.Absorption / 2), "heart/container.png",
                    "heart/absorbing_full.png", "heart/absorbing_half.png"), $"+{Half(profile.Absorption)}"));
        }

        Inventory = [.. profile.Inventory.Select(ItemText)];
        EnderChest = [.. profile.EnderChest.Select(ItemText)];
        Effects = [.. profile.Effects.Select(e => $"{e.DisplayName} {Roman(e.Level)}" + (e.SecondsLeft is { } s ? $" · {TimeSpan.FromSeconds(s):m\\:ss}" : " · ∞"))];

        Stats =
        [
            new("Tiempo jugado", profile.PlayTime is { } time ? FormatTime(time) : "—"),
            new("Muertes", Number(profile.Deaths)),
            new("Criaturas eliminadas", Number(profile.MobKills)),
            new("Jugadores eliminados", Number(profile.PlayerKills)),
            new("Distancia recorrida", profile.DistanceMeters is { } m ? (m >= 1000 ? $"{(m / 1000).ToString("0.#", Es)} km" : $"{m:0} m") : "—"),
            new("Saltos", Number(profile.Jumps)),
            new("Logros", Number(profile.Advancements)),
        ];
    }

    public PlayerProfile Profile { get; }
    public string Name => Profile.Name;
    public bool IsOnline { get; }
    public string Initial { get; }
    public string Subtitle => $"{Profile.GameModeName} · {(IsOnline ? "conectado" : "desconectado")}";
    public string SavedText => $"Datos del último guardado del mundo: {Profile.SavedAt.ToString("d MMM yyyy, HH:mm", Es)}";

    public IReadOnlyList<ProfileBar> Bars { get; }

    // Iconos del juego (si se pudieron extraer); si no, las barras.
    public bool ShowSprites => HudSprites.Available;
    public bool ShowBars => !HudSprites.Available;
    public List<HudRow> HudRows { get; private set; } = [];
    public ImageSource? XpBackground => HudSprites.Get("experience_bar_background.png");
    public ImageSource? XpBar => HudSprites.Get("experience_bar_progress.png");
    public double XpRatio => Profile.XpProgress;
    public string XpText => Bars[3].Value;
    public IReadOnlyList<ProfileFact> State { get; }
    public IReadOnlyList<ProfileFact> Location { get; }
    public List<ProfileFact> Equipment { get; }
    public bool HasEquipment => Equipment.Count > 0;
    public IReadOnlyList<string> Inventory { get; }
    public bool HasInventory => Inventory.Count > 0;
    public string InventoryTitle => $"Inventario ({Inventory.Count} de 36 huecos)";
    public IReadOnlyList<string> EnderChest { get; }
    public bool HasEnderChest => EnderChest.Count > 0;
    public string EnderChestTitle => $"Cofre de Ender ({EnderChest.Count} de 27 huecos)";
    public IReadOnlyList<string> Effects { get; }
    public bool HasEffects => Effects.Count > 0;
    public IReadOnlyList<ProfileFact> Stats { get; }

    /// <summary>Medio corazón = 1 punto: 19 → «9,5» corazones se muestra como puntos («19»), con un decimal si hace falta.</summary>
    public static string Half(float value) => value.ToString(value % 1 == 0 ? "0" : "0.#", Es);

    /// <summary>
    /// Iconos como en el HUD: cada uno vale 2 puntos (lleno, medio o vacío). El hambre se llena de derecha a
    /// izquierda, como en el juego. <paramref name="standalone"/>: la armadura no lleva contenedor, cada estado es su icono.
    /// </summary>
    private static List<HudIcon> Points(float value, int count, string empty, string full, string half, bool fromRight = false, bool standalone = false)
    {
        var icons = new List<HudIcon>();
        for (var i = 0; i < count; i++)
        {
            var index = fromRight ? count - 1 - i : i;
            var left = value - index * 2;
            var front = left >= 2 ? HudSprites.Get(full) : left >= 1 ? HudSprites.Get(half) : standalone ? HudSprites.Get(empty) : null;
            icons.Add(new HudIcon(standalone ? null : HudSprites.Get(empty), front));
        }

        return icons;
    }

    /// <summary>10 burbujas: 300 de aire = todas llenas.</summary>
    private static List<HudIcon> Bubbles(int air)
    {
        var full = (int)Math.Ceiling(Math.Clamp(air, 0, 300) * 10 / 300.0);
        return [.. Enumerable.Range(0, 10).Select(i => new HudIcon(null, HudSprites.Get(i < full ? "air.png" : "air_empty.png")))];
    }

    private static double Ratio(double value, double max) => max <= 0 ? 0 : Math.Clamp(value / max, 0, 1);

    private static string ItemText(PlayerItem item) =>
        item.DisplayName + (item.Count > 1 ? $" ×{item.Count}" : string.Empty) + (item.Damage > 0 ? $" (desgaste {item.Damage})" : string.Empty);

    private static string Number(int? value) => value?.ToString("N0", Es) ?? "—";

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min" : $"{time.Minutes} min";

    private static string Roman(int level) => level switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V",
        _ => level.ToString(CultureInfo.InvariantCulture),
    };
}
