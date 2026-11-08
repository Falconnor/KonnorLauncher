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
    public void Missing_or_damaged_whitelist_is_empty()
    {
        WhitelistFile.Load(_dir.Path).ShouldBeEmpty();
        WhitelistFile.Remove(_dir.Path, "Steve").ShouldBeFalse();
        File.WriteAllText(WhitelistFile.PathFor(_dir.Path), "no es json");
        WhitelistFile.Load(_dir.Path).ShouldBeEmpty();
    }
}
