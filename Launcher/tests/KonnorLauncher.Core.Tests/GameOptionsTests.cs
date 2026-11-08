using KonnorLauncher.Core.Launching;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

public sealed class GameOptionsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Accessibility_menu_is_skipped_keeping_the_other_options()
    {
        var file = Path.Combine(_dir.Path, GameOptions.FileName);

        GameOptions.SkipAccessibilityOnboarding(_dir.Path); // instancia nueva: no hay options.txt
        File.ReadAllLines(file).ShouldBe(["onboardAccessibility:false"]);

        File.WriteAllLines(file, ["fov:0.5", "onboardAccessibility:true", "lang:es_es"]);
        GameOptions.SkipAccessibilityOnboarding(_dir.Path);
        File.ReadAllLines(file).ShouldBe(["fov:0.5", "onboardAccessibility:false", "lang:es_es"]);

        var written = File.GetLastWriteTimeUtc(file);
        File.SetLastWriteTimeUtc(file, written.AddDays(-1));
        GameOptions.SkipAccessibilityOnboarding(_dir.Path); // ya estaba: no se reescribe
        File.GetLastWriteTimeUtc(file).ShouldBe(written.AddDays(-1));
    }
}
