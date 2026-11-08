using fNbt;
using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Perfil de un jugador leído del mundo del servidor (Hosting → Jugadores).</summary>
public sealed class PlayerProfileTests : IDisposable
{
    private static readonly Guid Steve = Guid.Parse("8075ff90-e98c-37a7-838f-650f9ebc0102");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static void Save(string path, NbtCompound root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new NbtFile(root).SaveToFile(path, NbtCompression.GZip);
    }

    private static NbtCompound Item(string id, int count, string? slotName = null, int slot = -1, int damage = 0)
    {
        var item = slotName is null ? new NbtCompound() : new NbtCompound(slotName);
        item.Add(new NbtString("id", id));
        item.Add(new NbtInt("count", count));
        if (slot >= 0)
            item.Add(new NbtByte("Slot", (byte)slot));
        if (damage > 0)
            item.Add(new NbtCompound("components") { new NbtInt("minecraft:damage", damage) });
        return item;
    }

    [Fact]
    public void Reads_the_26x_format_with_equipment_respawn_stats_and_advancements()
    {
        var server = _dir.Path;
        var world = Path.Combine(server, "world");
        Save(Path.Combine(world, "level.dat"), new NbtCompound("")
        {
            new NbtCompound("Data")
            {
                new NbtCompound("spawn") { new NbtIntArray("pos", [-144, 71, -48]), new NbtString("dimension", "minecraft:overworld") },
            },
        });
        Save(Path.Combine(world, "players", "data", Steve + ".dat"), new NbtCompound("")
        {
            new NbtFloat("Health", 17),
            new NbtInt("foodLevel", 14),
            new NbtFloat("foodSaturationLevel", 2.5f),
            new NbtInt("XpLevel", 12),
            new NbtFloat("XpP", 0.5f),
            new NbtInt("playerGameType", 0),
            new NbtString("Dimension", "minecraft:the_nether"),
            new NbtList("Pos", [new NbtDouble(10.7), new NbtDouble(64), new NbtDouble(-3.2)]),
            new NbtInt("SelectedItemSlot", 0),
            new NbtList("Inventory", [Item("minecraft:diamond_sword", 1, slot: 0, damage: 12), Item("minecraft:bread", 5, slot: 1)]),
            new NbtList("EnderItems", [Item("minecraft:emerald", 30, slot: 0)]),
            new NbtCompound("equipment") { Item("minecraft:iron_helmet", 1, "head"), Item("minecraft:diamond_chestplate", 1, "chest"), Item("minecraft:shield", 1, "offhand") },
            new NbtCompound("respawn") { new NbtIntArray("pos", [5, 70, 6]), new NbtString("dimension", "minecraft:overworld") },
            new NbtCompound("LastDeathLocation") { new NbtIntArray("pos", [-145, 65, -106]), new NbtString("dimension", "minecraft:overworld") },
            new NbtList("active_effects", [new NbtCompound { new NbtString("id", "minecraft:speed"), new NbtByte("amplifier", 1), new NbtInt("duration", 600) }]),
            new NbtCompound("abilities") { new NbtByte("flying", 0), new NbtByte("mayfly", 1) },
        });
        File.WriteAllText(Path.Combine(world, "players", "stats", Steve + ".json").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)),
            """{"stats":{"minecraft:custom":{"minecraft:play_time":144000,"minecraft:deaths":2,"minecraft:walk_one_cm":150000,"minecraft:sprint_one_cm":50000,"minecraft:jump":40}}}""");
        File.WriteAllText(Path.Combine(world, "players", "advancements", Steve + ".json").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)),
            """{"minecraft:story/root":{"done":true},"minecraft:story/mine_stone":{"done":false},"minecraft:recipes/misc/x":{"done":true},"DataVersion":5023}""");

        var p = PlayerProfileReader.Read(server, "Steve", Steve).ShouldNotBeNull();

        p.Health.ShouldBe(17);
        p.Food.ShouldBe(14);
        p.XpLevel.ShouldBe(12);
        p.Armor.ShouldBe(2 + 8); // casco de hierro + peto de diamante
        p.Position!.Coordinates.ShouldBe("10, 64, -4");
        p.Position.DimensionName.ShouldBe("Nether");
        p.Respawn!.Coordinates.ShouldBe("5, 70, 6");
        p.WorldSpawn!.Coordinates.ShouldBe("-144, 71, -48");
        p.LastDeath!.Coordinates.ShouldBe("-145, 65, -106");
        p.HeldItem!.DisplayName.ShouldBe("Diamond sword");
        p.HeldItem.Damage.ShouldBe(12);
        p.Inventory.Count.ShouldBe(2);
        p.EnderChest.ShouldHaveSingleItem().Count.ShouldBe(30);
        p.Equipment.Keys.ShouldBe(["head", "chest", "offhand"], ignoreOrder: true);
        p.Effects.ShouldHaveSingleItem().ShouldBe(new PlayerEffect("minecraft:speed", 2, 30));
        p.CanFly.ShouldBeTrue();
        p.PlayTime.ShouldBe(TimeSpan.FromHours(2));
        p.Deaths.ShouldBe(2);
        p.DistanceMeters.ShouldBe(2000);
        p.Advancements.ShouldBe(1); // sin recetas ni los no conseguidos
    }

    [Fact]
    public void Reads_the_old_format_with_armor_slots_and_spawn_coordinates()
    {
        var server = _dir.Path;
        File.WriteAllText(Path.Combine(server, "server.properties"), "level-name=mundo\n");
        Save(Path.Combine(server, "mundo", "playerdata", Steve + ".dat"), new NbtCompound("")
        {
            new NbtFloat("Health", 20),
            new NbtInt("Dimension", -1),
            new NbtList("Pos", [new NbtDouble(1), new NbtDouble(2), new NbtDouble(3)]),
            new NbtList("Inventory",
            [
                new NbtCompound { new NbtString("id", "minecraft:netherite_boots"), new NbtByte("Count", 1), new NbtByte("Slot", 100) },
                new NbtCompound { new NbtString("id", "minecraft:torch"), new NbtByte("Count", 64), new NbtByte("Slot", 3) },
            ]),
            new NbtInt("SpawnX", 7), new NbtInt("SpawnY", 64), new NbtInt("SpawnZ", 8),
        });

        var p = PlayerProfileReader.Read(server, "Steve", Steve).ShouldNotBeNull();

        p.Armor.ShouldBe(3);
        p.Equipment["feet"].Id.ShouldBe("minecraft:netherite_boots");
        p.Inventory.ShouldHaveSingleItem().Count.ShouldBe(64); // las botas no cuentan como inventario
        p.Position!.DimensionName.ShouldBe("Nether");
        p.Respawn!.Coordinates.ShouldBe("7, 64, 8");
        p.Deaths.ShouldBeNull(); // sin archivo de estadísticas
        PlayerHistory.ReadUserCache(Path.Combine(server, "usercache.json")).ShouldBeEmpty();
    }

    [Fact]
    public void Missing_or_damaged_data_gives_no_profile()
    {
        PlayerProfileReader.Read(_dir.Path, "Steve", Steve).ShouldBeNull();
        var file = Path.Combine(_dir.Path, "world", "players", "data", Steve + ".dat");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "no es nbt");
        PlayerProfileReader.Read(_dir.Path, "Steve", Steve).ShouldBeNull();
    }

    [Theory]
    [InlineData("head", "minecraft:turtle_helmet", 2)]
    [InlineData("legs", "minecraft:leather_leggings", 2)]
    [InlineData("chest", "minecraft:copper_chestplate", 4)]
    [InlineData("head", "minecraft:diamond_chestplate", 0)] // en otro hueco no protege
    [InlineData("feet", "create:copper_diving_boots", 0)] // de un mod: desconocido
    public void Armor_points_per_piece(string slot, string id, int points) =>
        PlayerProfileReader.ArmorPoints(slot, id).ShouldBe(points);
}

internal static class PathExtensions
{
    public static string Also(this string path, Action<string> action)
    {
        action(path);
        return path;
    }
}
