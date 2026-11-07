using KonnorLauncher.Core.Players;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Skins descargadas sin aplicar (sección Descargadas).</summary>
public sealed class SkinLibraryTests : IDisposable
{
    private const string HashA = "a3b895662b59a489bcfdae452142d1c91ecb116f1a48f0442ea40d7b2888f38a";
    private const string HashB = "34c67eb4f7664040146736a36d0f762c23fb08a8a403df7dac8d5888286e4617";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private SkinLibrary Library() => new(_dir.Combine("player", "skins"));

    [Fact]
    public void Empty_library_has_no_skins()
    {
        Library().LoadAll().ShouldBeEmpty();
        Library().Contains(HashA).ShouldBeFalse();
    }

    [Fact]
    public void Added_skin_is_copied_and_listed_with_its_name_and_model()
    {
        var source = _dir.Write("cache/a.png", "png A");

        Library().Add(source, HashA, "  Steve rojo ", slim: true, uuid: "u1");

        var skins = Library().LoadAll(); // otra instancia: lo lee del disco
        skins.Count.ShouldBe(1);
        skins[0].Texture.ShouldBe(HashA);
        skins[0].Name.ShouldBe("Steve rojo");
        skins[0].Slim.ShouldBeTrue();
        skins[0].Uuid.ShouldBe("u1");
        File.ReadAllText(Library().TexturePath(HashA)).ShouldBe("png A");
        File.Exists(source).ShouldBeTrue(); // se copia, no se mueve
        Library().Contains(HashA).ShouldBeTrue();
    }

    [Fact]
    public void Newest_first_and_the_same_skin_is_kept_once()
    {
        var library = Library();
        library.Add(_dir.Write("cache/a.png", "A"), HashA, "Primera", slim: false, uuid: null);
        library.Add(_dir.Write("cache/b.png", "B"), HashB, "Segunda", slim: false, uuid: null);
        library.Add(_dir.Write("cache/a2.png", "A2"), HashA, "Primera otra vez", slim: true, uuid: null);

        var skins = library.LoadAll();

        skins.Select(s => s.Name).ShouldBe(["Primera otra vez", "Segunda"]);
        skins[0].Slim.ShouldBeTrue();
        File.ReadAllText(library.TexturePath(HashA)).ShouldBe("A2");
    }

    [Fact]
    public void Skins_whose_file_was_deleted_are_not_listed()
    {
        var library = Library();
        library.Add(_dir.Write("cache/a.png", "A"), HashA, "A", slim: false, uuid: null);
        library.Add(_dir.Write("cache/b.png", "B"), HashB, "B", slim: false, uuid: null);
        File.Delete(library.TexturePath(HashB));

        library.LoadAll().Select(s => s.Texture).ShouldBe([HashA]);
        library.Contains(HashB).ShouldBeFalse();
    }

    [Fact]
    public void Damaged_index_counts_as_empty_and_is_rewritten_on_add()
    {
        var library = Library();
        _dir.Write("player/skins/" + SkinLibrary.IndexFileName, "{ no es json");

        library.LoadAll().ShouldBeEmpty();
        library.Add(_dir.Write("cache/a.png", "A"), HashA, null, slim: false, uuid: null);
        library.LoadAll().Single().Name.ShouldBeNull();
    }

    [Fact]
    public void Removed_skin_loses_its_file_and_entry_and_the_rest_stay()
    {
        var library = Library();
        library.Add(_dir.Write("cache/a.png", "A"), HashA, "A", slim: false, uuid: null);
        library.Add(_dir.Write("cache/b.png", "B"), HashB, "B", slim: false, uuid: null);

        library.Remove(HashA).ShouldBeTrue();

        File.Exists(library.TexturePath(HashA)).ShouldBeFalse();
        Library().LoadAll().Select(s => s.Texture).ShouldBe([HashB]);
        library.Remove(HashA).ShouldBeFalse(); // ya no estaba
    }

    [Fact]
    public void Remove_with_an_invalid_texture_touches_nothing()
    {
        var library = Library();
        library.Add(_dir.Write("cache/a.png", "A"), HashA, "A", slim: false, uuid: null);

        library.Remove("../" + SkinLibrary.IndexFileName).ShouldBeFalse();

        library.LoadAll().Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("../../fuera")]
    [InlineData("ABC")]
    [InlineData("")]
    public void Invalid_textures_never_become_a_path(string texture)
    {
        Should.Throw<ArgumentException>(() => Library().TexturePath(texture));
        Should.Throw<ArgumentException>(() => Library().Add(_dir.Write("cache/x.png", "x"), texture, "x", false, null));
        Library().Contains(texture).ShouldBeFalse();
    }
}
