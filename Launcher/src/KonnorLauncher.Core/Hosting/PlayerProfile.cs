using System.Globalization;
using System.Text.Json;
using fNbt;

namespace KonnorLauncher.Core.Hosting;

/// <summary>Objeto de un inventario: <c>minecraft:diamond_sword</c>, cantidad y daño (durabilidad gastada).</summary>
public sealed record PlayerItem(string Id, int Count, int Slot = -1, int Damage = 0)
{
    /// <summary><c>minecraft:diamond_sword</c> → «Diamond sword» (los mods conservan su espacio de nombres: «Create: Wrench»).</summary>
    public string DisplayName => PlayerProfile.Humanize(Id);
}

/// <summary>Efecto activo: <c>minecraft:speed</c>, nivel (1 = I) y segundos que quedan (null = infinito).</summary>
public sealed record PlayerEffect(string Id, int Level, int? SecondsLeft)
{
    public string DisplayName => PlayerProfile.Humanize(Id);
}

/// <summary>Punto con dimensión (posición, reaparición, última muerte).</summary>
public sealed record PlayerLocation(double X, double Y, double Z, string Dimension)
{
    public string Coordinates => string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(X):0}, {Math.Floor(Y):0}, {Math.Floor(Z):0}");
    public string DimensionName => PlayerProfile.DimensionName(Dimension);
}

/// <summary>
/// Estado de un jugador según el mundo del servidor: lo que guarda Minecraft en
/// <c>&lt;mundo&gt;/players/data/&lt;uuid&gt;.dat</c> (<c>playerdata</c> antes de la 26.1), más sus estadísticas y logros.
/// Son los datos del último guardado del mundo (al salir el jugador, cada pocos minutos o con <c>save-all</c>).
/// </summary>
public sealed record PlayerProfile
{
    public required string Name { get; init; }
    public required Guid Uuid { get; init; }
    public DateTimeOffset SavedAt { get; init; }

    public float Health { get; init; }
    public float MaxHealth { get; init; } = 20;
    public float Absorption { get; init; }
    public int Food { get; init; }
    public float Saturation { get; init; }
    public int Armor { get; init; }
    public int Air { get; init; } = 300;
    public bool OnFire { get; init; }

    public int XpLevel { get; init; }
    /// <summary>Progreso hacia el siguiente nivel (0 a 1).</summary>
    public float XpProgress { get; init; }
    public int XpTotal { get; init; }
    public int Score { get; init; }

    /// <summary>0 supervivencia, 1 creativo, 2 aventura, 3 espectador.</summary>
    public int GameMode { get; init; }
    public bool Flying { get; init; }
    public bool CanFly { get; init; }
    public bool Invulnerable { get; init; }

    public PlayerLocation? Position { get; init; }
    /// <summary>Cama o ancla de reaparición; null = el spawn del mundo (<see cref="WorldSpawn"/>).</summary>
    public PlayerLocation? Respawn { get; init; }
    public PlayerLocation? WorldSpawn { get; init; }
    public PlayerLocation? LastDeath { get; init; }

    /// <summary>Casco, peto, pantalones, botas y mano secundaria (los que lleva).</summary>
    public IReadOnlyDictionary<string, PlayerItem> Equipment { get; init; } = new Dictionary<string, PlayerItem>();
    public IReadOnlyList<PlayerItem> Inventory { get; init; } = [];
    public IReadOnlyList<PlayerItem> EnderChest { get; init; } = [];
    public PlayerItem? HeldItem { get; init; }
    public IReadOnlyList<PlayerEffect> Effects { get; init; } = [];

    // Estadísticas (stats/<uuid>.json) y logros (advancements/<uuid>.json); null si no hay archivo.
    public TimeSpan? PlayTime { get; init; }
    public int? Deaths { get; init; }
    public int? MobKills { get; init; }
    public int? PlayerKills { get; init; }
    /// <summary>Recorrido a pie, nadando, en vehículos, volando… en metros.</summary>
    public double? DistanceMeters { get; init; }
    public int? Jumps { get; init; }
    public int? Advancements { get; init; }

    public string GameModeName => GameMode switch
    {
        1 => "Creativo",
        2 => "Aventura",
        3 => "Espectador",
        _ => "Supervivencia",
    };

    public static string Humanize(string id)
    {
        var (ns, path) = id.Contains(':') ? (id[..id.IndexOf(':')], id[(id.IndexOf(':') + 1)..]) : ("minecraft", id);
        var words = path.Replace('/', ' ').Replace('_', ' ').Trim();
        var text = words.Length == 0 ? id : char.ToUpperInvariant(words[0]) + words[1..];
        return ns is "minecraft" or "" ? text : $"{char.ToUpperInvariant(ns[0])}{ns[1..]}: {text}";
    }

    public static string DimensionName(string dimension) => dimension switch
    {
        "minecraft:overworld" => "Mundo normal",
        "minecraft:the_nether" => "Nether",
        "minecraft:the_end" => "End",
        _ => Humanize(dimension),
    };
}

/// <summary>Lee <see cref="PlayerProfile"/> de los archivos del mundo (con fNbt).</summary>
public static class PlayerProfileReader
{
    /// <summary>Puntos de armadura de cada pieza (los del juego), por material y hueco.</summary>
    private static readonly Dictionary<string, int[]> ArmorByMaterial = new()
    {
        // casco, peto, pantalones, botas
        ["leather"] = [1, 3, 2, 1],
        ["copper"] = [2, 4, 3, 1],
        ["chainmail"] = [2, 5, 4, 1],
        ["golden"] = [2, 5, 3, 1],
        ["iron"] = [2, 6, 5, 2],
        ["diamond"] = [3, 8, 6, 3],
        ["netherite"] = [3, 8, 6, 3],
    };

    private static readonly string[] ArmorSlots = ["head", "chest", "legs", "feet"];
    private static readonly string[] ArmorSuffixes = ["_helmet", "_chestplate", "_leggings", "_boots"];

    /// <summary>Carpetas de un jugador: la de 26.1 (<c>players/data</c>…) y la anterior (<c>playerdata</c>…).</summary>
    public static (string Data, string Stats, string Advancements)[] Layouts(string world) =>
    [
        (Path.Combine(world, "players", "data"), Path.Combine(world, "players", "stats"), Path.Combine(world, "players", "advancements")),
        (Path.Combine(world, "playerdata"), Path.Combine(world, "stats"), Path.Combine(world, "advancements")),
    ];

    public static string WorldDir(string serverDir)
    {
        var level = ServerPropertiesFile.Load(Path.Combine(serverDir, ServerPropertiesFile.FileName)).Get("level-name", "world");
        return Path.Combine(serverDir, string.IsNullOrWhiteSpace(level) ? "world" : level);
    }

    /// <summary>Archivo de datos del jugador (el más reciente si hay de las dos versiones); null si nunca entró.</summary>
    public static string? FindDataFile(string serverDir, Guid uuid) =>
        Layouts(WorldDir(serverDir))
            .Select(l => Path.Combine(l.Data, uuid + ".dat"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    /// <summary>Perfil del jugador; null si no hay datos o el archivo está dañado.</summary>
    public static PlayerProfile? Read(string serverDir, string name, Guid uuid)
    {
        var dataFile = FindDataFile(serverDir, uuid);
        if (dataFile is null)
            return null;

        NbtCompound root;
        try
        {
            var file = new NbtFile();
            file.LoadFromFile(dataFile);
            root = file.RootTag;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NbtFormatException or EndOfStreamException
                                       or InvalidCastException)
        {
            return null;
        }

        var world = WorldDir(serverDir);
        var layout = Layouts(world).First(l => dataFile.StartsWith(l.Data, StringComparison.OrdinalIgnoreCase));
        var inventory = Items(root.Get<NbtList>("Inventory"));
        var equipment = Equipment(root, inventory);
        var selected = Int(root, "SelectedItemSlot");
        var abilities = root.Get<NbtCompound>("abilities");
        var stats = ReadStats(Path.Combine(layout.Stats, uuid + ".json"));

        return new PlayerProfile
        {
            Name = name,
            Uuid = uuid,
            SavedAt = File.GetLastWriteTime(dataFile),
            Health = Float(root, "Health"),
            MaxHealth = MaxHealth(root),
            Absorption = Float(root, "AbsorptionAmount"),
            Food = Int(root, "foodLevel", 20),
            Saturation = Float(root, "foodSaturationLevel"),
            Armor = equipment.Sum(e => ArmorPoints(e.Key, e.Value.Id)),
            Air = Int(root, "Air", 300),
            OnFire = Int(root, "Fire", -20) > 0,
            XpLevel = Int(root, "XpLevel"),
            XpProgress = Math.Clamp(Float(root, "XpP"), 0, 1),
            XpTotal = Int(root, "XpTotal"),
            Score = Int(root, "Score"),
            GameMode = Int(root, "playerGameType"),
            Flying = abilities is not null && Int(abilities, "flying") != 0,
            CanFly = abilities is not null && Int(abilities, "mayfly") != 0,
            Invulnerable = abilities is not null && Int(abilities, "invulnerable") != 0,
            Position = Position(root),
            Respawn = Respawn(root),
            WorldSpawn = WorldSpawn(world),
            LastDeath = root.Get<NbtCompound>("LastDeathLocation") is { } death ? Location(death) : null,
            Equipment = equipment,
            Inventory = [.. inventory.Where(i => i.Slot is >= 0 and < 36).OrderBy(i => i.Slot)],
            EnderChest = Items(root.Get<NbtList>("EnderItems")),
            HeldItem = inventory.FirstOrDefault(i => i.Slot == selected),
            Effects = Effects(root),
            PlayTime = stats.TryGetValue("play_time", out var ticks) ? TimeSpan.FromSeconds(ticks / 20.0) : null,
            Deaths = stats.TryGetValue("deaths", out var deaths) ? (int)deaths : stats.Count > 0 ? 0 : null,
            MobKills = stats.TryGetValue("mob_kills", out var mobs) ? (int)mobs : stats.Count > 0 ? 0 : null,
            PlayerKills = stats.TryGetValue("player_kills", out var players) ? (int)players : stats.Count > 0 ? 0 : null,
            DistanceMeters = stats.Count > 0 ? stats.Where(s => s.Key.EndsWith("_one_cm", StringComparison.Ordinal)).Sum(s => s.Value) / 100.0 : null,
            Jumps = stats.TryGetValue("jump", out var jumps) ? (int)jumps : stats.Count > 0 ? 0 : null,
            Advancements = CountAdvancements(Path.Combine(layout.Advancements, uuid + ".json")),
        };
    }

    // ── Valores ──────────────────────────────────────────────────────────

    private static int Int(NbtCompound c, string name, int fallback = 0) =>
        c.TryGet(name, out NbtTag? tag) && tag is not null && tag.TagType is NbtTagType.Byte or NbtTagType.Short or NbtTagType.Int or NbtTagType.Long ? tag.IntValue : fallback;

    private static float Float(NbtCompound c, string name, float fallback = 0) =>
        c.TryGet(name, out NbtTag? tag) && tag is not null && tag.TagType is NbtTagType.Float or NbtTagType.Double or NbtTagType.Int or NbtTagType.Short or NbtTagType.Byte
            ? tag.FloatValue
            : fallback;

    private static string? String(NbtCompound c, string name) =>
        c.TryGet(name, out NbtTag? tag) && tag is not null && tag.TagType == NbtTagType.String ? tag.StringValue : null;

    /// <summary>Vida máxima: el atributo <c>max_health</c> si se cambió (por ejemplo con un mod), si no 20.</summary>
    private static float MaxHealth(NbtCompound root)
    {
        foreach (var attribute in root.Get<NbtList>("attributes")?.OfType<NbtCompound>() ?? root.Get<NbtList>("Attributes")?.OfType<NbtCompound>() ?? [])
        {
            var id = String(attribute, "id") ?? String(attribute, "Name");
            if (id is "minecraft:max_health" or "minecraft:generic.max_health" or "generic.maxHealth")
                return Float(attribute, "base", Float(attribute, "Base", 20));
        }

        return 20;
    }

    private static PlayerLocation? Position(NbtCompound root)
    {
        if (root.Get<NbtList>("Pos") is not { Count: 3 } pos)
            return null;
        return new PlayerLocation(pos[0].DoubleValue, pos[1].DoubleValue, pos[2].DoubleValue, String(root, "Dimension") ?? DimensionFromId(Int(root, "Dimension")));
    }

    /// <summary>Antes de la 1.16 la dimensión era un número.</summary>
    private static string DimensionFromId(int id) => id switch
    {
        -1 => "minecraft:the_nether",
        1 => "minecraft:the_end",
        _ => "minecraft:overworld",
    };

    /// <summary><c>{pos: [I; x, y, z], dimension}</c> (formato de 1.21.5 en adelante y de <c>LastDeathLocation</c>).</summary>
    private static PlayerLocation? Location(NbtCompound c) =>
        c.TryGet("pos", out NbtTag? tag) && tag is NbtIntArray { Value.Length: 3 } array
            ? new PlayerLocation(array.Value[0], array.Value[1], array.Value[2], String(c, "dimension") ?? "minecraft:overworld")
            : null;

    /// <summary>Cama o ancla: <c>respawn</c> (1.21.5+) o <c>SpawnX/Y/Z</c> y <c>SpawnDimension</c> (antes).</summary>
    private static PlayerLocation? Respawn(NbtCompound root)
    {
        if (root.Get<NbtCompound>("respawn") is { } respawn)
            return Location(respawn);
        if (root.Contains("SpawnX") && root.Contains("SpawnY") && root.Contains("SpawnZ"))
            return new PlayerLocation(Int(root, "SpawnX"), Int(root, "SpawnY"), Int(root, "SpawnZ"), String(root, "SpawnDimension") ?? "minecraft:overworld");
        return null;
    }

    /// <summary>Spawn del mundo en <c>level.dat</c>: <c>Data.spawn</c> (26.x) o <c>Data.SpawnX/Y/Z</c> (antes).</summary>
    public static PlayerLocation? WorldSpawn(string world)
    {
        try
        {
            var file = new NbtFile();
            file.LoadFromFile(Path.Combine(world, "level.dat"));
            if (file.RootTag.Get<NbtCompound>("Data") is not { } data)
                return null;
            if (data.Get<NbtCompound>("spawn") is { } spawn)
                return Location(spawn);
            return data.Contains("SpawnX")
                ? new PlayerLocation(Int(data, "SpawnX"), Int(data, "SpawnY"), Int(data, "SpawnZ"), "minecraft:overworld")
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NbtFormatException or EndOfStreamException
                                       or InvalidCastException)
        {
            return null;
        }
    }

    private static List<PlayerItem> Items(NbtList? list)
    {
        var result = new List<PlayerItem>();
        foreach (var item in list?.OfType<NbtCompound>() ?? [])
        {
            if (Item(item) is { } parsed)
                result.Add(parsed with { Slot = Int(item, "Slot", -1) });
        }

        return result;
    }

    /// <summary><c>{id, count}</c> (1.20.5+) o <c>{id, Count}</c> (antes), con el daño de <c>components</c> o <c>tag.Damage</c>.</summary>
    private static PlayerItem? Item(NbtCompound item)
    {
        if (String(item, "id") is not { Length: > 0 } id)
            return null;
        var damage = item.Get<NbtCompound>("components") is { } components ? Int(components, "minecraft:damage")
            : item.Get<NbtCompound>("tag") is { } tag ? Int(tag, "Damage")
            : 0;
        return new PlayerItem(id, Math.Max(1, Int(item, "count", Int(item, "Count", 1))), Damage: damage);
    }

    /// <summary>
    /// Lo que lleva puesto: <c>equipment</c> (1.21.5+: <c>head</c>, <c>chest</c>, <c>legs</c>, <c>feet</c>, <c>offhand</c>) o los
    /// huecos 100-103 y -106 del inventario (antes).
    /// </summary>
    private static Dictionary<string, PlayerItem> Equipment(NbtCompound root, List<PlayerItem> inventory)
    {
        var result = new Dictionary<string, PlayerItem>();
        if (root.Get<NbtCompound>("equipment") is { } equipment)
        {
            foreach (var tag in equipment.OfType<NbtCompound>())
            {
                if (tag.Name is { } slot && Item(tag) is { } item)
                    result[slot] = item;
            }

            return result;
        }

        foreach (var item in inventory)
        {
            var slot = item.Slot switch { 103 => "head", 102 => "chest", 101 => "legs", 100 => "feet", -106 => "offhand", _ => null };
            if (slot is not null)
                result[slot] = item;
        }

        return result;
    }

    /// <summary>Puntos de armadura de una pieza en su hueco (los de mods no se conocen: 0).</summary>
    public static int ArmorPoints(string slot, string id)
    {
        var index = Array.IndexOf(ArmorSlots, slot);
        if (index < 0)
            return 0;
        var path = id.StartsWith("minecraft:", StringComparison.Ordinal) ? id["minecraft:".Length..] : null;
        if (path is null)
            return 0;
        if (path == "turtle_helmet" && index == 0)
            return 2;
        if (!path.EndsWith(ArmorSuffixes[index], StringComparison.Ordinal))
            return 0;
        var material = path[..^ArmorSuffixes[index].Length];
        return ArmorByMaterial.TryGetValue(material, out var points) ? points[index] : 0;
    }

    /// <summary><c>active_effects</c> (1.20.2+: <c>id</c> de texto) o <c>ActiveEffects</c> (antes: <c>Id</c> numérico).</summary>
    private static List<PlayerEffect> Effects(NbtCompound root)
    {
        var result = new List<PlayerEffect>();
        foreach (var effect in root.Get<NbtList>("active_effects")?.OfType<NbtCompound>() ?? root.Get<NbtList>("ActiveEffects")?.OfType<NbtCompound>() ?? [])
        {
            var id = String(effect, "id") ?? $"efecto {Int(effect, "Id")}";
            var duration = Int(effect, "duration", Int(effect, "Duration"));
            result.Add(new PlayerEffect(id, Int(effect, "amplifier", Int(effect, "Amplifier")) + 1, duration < 0 ? null : duration / 20));
        }

        return result;
    }

    // ── Estadísticas y logros ────────────────────────────────────────────

    /// <summary><c>stats.minecraft:custom</c> sin el prefijo <c>minecraft:</c> (<c>play_time</c>, <c>deaths</c>, <c>walk_one_cm</c>…).</summary>
    private static Dictionary<string, long> ReadStats(string path)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path))
                return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("stats", out var stats) && stats.TryGetProperty("minecraft:custom", out var custom)
                && custom.ValueKind == JsonValueKind.Object)
            {
                foreach (var stat in custom.EnumerateObject())
                {
                    if (stat.Value.TryGetInt64(out var value))
                        result[stat.Name.Replace("minecraft:", string.Empty)] = value;
                }

                // Antes de la 1.17 el tiempo de juego se llamaba play_one_minute (en ticks igualmente).
                if (!result.ContainsKey("play_time") && result.TryGetValue("play_one_minute", out var old))
                    result["play_time"] = old;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }

        return result;
    }

    /// <summary>Logros conseguidos (sin las recetas desbloqueadas, que también se guardan ahí).</summary>
    private static int? CountAdvancements(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.EnumerateObject().Count(a =>
                a.Value.ValueKind == JsonValueKind.Object
                && !a.Name.Contains(":recipes/", StringComparison.Ordinal)
                && a.Value.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
