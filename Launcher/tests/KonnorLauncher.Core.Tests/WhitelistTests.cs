using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

public sealed class WhitelistTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Lists_and_removes_players_from_whitelist_json()
    {
        File.WriteAllText(WhitelistFile.PathFor(_dir.Path), """
            [{"uuid":"a-1","name":"steve"},{"uuid":"b-2","name":"Alex"},{"uuid":"c-3"}]
            """);

        WhitelistFile.Load(_dir.Path).Select(e => e.Name).ShouldBe(["Alex", "steve"]); // sin nombre: se ignora

        WhitelistFile.Remove(_dir.Path, "STEVE").ShouldBeTrue(); // sin distinguir mayúsculas
        WhitelistFile.Remove(_dir.Path, "Nadie").ShouldBeFalse();
        WhitelistFile.Load(_dir.Path).ShouldBe([new WhitelistEntry("Alex", "b-2")]);
    }

    [Fact]
    public void Banned_players_have_reason_source_and_can_be_pardoned()
    {
        File.WriteAllText(BannedPlayersFile.PathFor(_dir.Path), """
            [{"uuid":"a-1","name":"Griefer","created":"2026-11-07 20:00:00 -0300","source":"Server","expires":"forever","reason":"Romper casas"},
             {"uuid":"b-2","name":"Temporal","source":"Admin","expires":"2026-12-01 00:00:00 -0300","reason":"Spam"}]
            """);

        var banned = BannedPlayersFile.Load(_dir.Path);
        banned.Select(e => e.Details).ShouldBe(["Romper casas · por Server", "Spam · por Admin · hasta 2026-12-01 00:00:00 -0300"]);

        BannedPlayersFile.Remove(_dir.Path, "griefer").ShouldBeTrue();
        BannedPlayersFile.Load(_dir.Path).ShouldHaveSingleItem().Name.ShouldBe("Temporal");
    }

    [Fact]
    public void Missing_or_damaged_whitelist_is_empty()
    {
        WhitelistFile.Load(_dir.Path).ShouldBeEmpty();
        WhitelistFile.Remove(_dir.Path, "Steve").ShouldBeFalse();
        File.WriteAllText(WhitelistFile.PathFor(_dir.Path), "no es json");
        WhitelistFile.Load(_dir.Path).ShouldBeEmpty();
    }
}
