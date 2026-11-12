using System.Text.Json.Nodes;
using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Listas de jugadores del servidor editadas con el servidor detenido: lista blanca, baneados y operadores.</summary>
public class PlayerListsTests
{
    private static readonly Guid Steve = Guid.Parse("8667ba71-b85a-4004-af54-457a9734eed7");

    [Fact]
    public void Whitelist_add_and_remove_toggle_the_player()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);

        WhitelistFile.Add(dir.Path, "Steve", Steve).ShouldBeTrue();
        WhitelistFile.Add(dir.Path, "steve", Steve).ShouldBeFalse(); // ya estaba (sin distinguir mayúsculas)
        PlayerListFile.Contains(WhitelistFile.PathFor(dir.Path), "STEVE").ShouldBeTrue();
        WhitelistFile.Load(dir.Path).ShouldHaveSingleItem().ShouldBe(new WhitelistEntry("Steve", Steve.ToString("D")));

        WhitelistFile.Remove(dir.Path, "Steve").ShouldBeTrue();
        PlayerListFile.Contains(WhitelistFile.PathFor(dir.Path), "Steve").ShouldBeFalse();
    }

    [Fact]
    public void A_ban_has_the_fields_the_server_writes()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 11, 11, 21, 30, 5, TimeSpan.FromHours(1));

        BannedPlayersFile.Add(dir.Path, "Steve", Steve, now).ShouldBeTrue();

        var entry = (JsonObject)JsonNode.Parse(File.ReadAllText(BannedPlayersFile.PathFor(dir.Path)))!.AsArray().Single()!;
        entry["uuid"]!.GetValue<string>().ShouldBe("8667ba71-b85a-4004-af54-457a9734eed7");
        entry["name"]!.GetValue<string>().ShouldBe("Steve");
        entry["created"]!.GetValue<string>().ShouldBe("2026-11-11 21:30:05 +0100");
        entry["expires"]!.GetValue<string>().ShouldBe("forever");
        BannedPlayersFile.Load(dir.Path).Single().Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Operators_get_level_4_and_keep_the_other_entries()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(OpsFile.PathFor(dir.Path), """[{"uuid":"00000000-0000-0000-0000-000000000001","name":"Alex","level":2,"bypassesPlayerLimit":true}]""");

        OpsFile.Add(dir.Path, "Steve", Steve).ShouldBeTrue();

        var ops = JsonNode.Parse(File.ReadAllText(OpsFile.PathFor(dir.Path)))!.AsArray().OfType<JsonObject>().ToList();
        ops.Count.ShouldBe(2);
        ops.Single(o => o["name"]!.GetValue<string>() == "Alex")["level"]!.GetValue<int>().ShouldBe(2);
        ops.Single(o => o["name"]!.GetValue<string>() == "Steve")["level"]!.GetValue<int>().ShouldBe(4);

        OpsFile.Remove(dir.Path, "Steve").ShouldBeTrue();
        OpsFile.Load(dir.Path).Select(o => o.Name).ShouldBe(["Alex"]);
    }
}
