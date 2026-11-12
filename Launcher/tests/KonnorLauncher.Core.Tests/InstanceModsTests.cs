using System.IO.Compression;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Mods de una instancia: lista, activar/desactivar (.jar.disabled) e icono de dentro del jar.</summary>
public class InstanceModsTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    [Fact]
    public void Lists_enabled_and_disabled_mods_by_name()
    {
        using var dir = new TempDir();
        var mods = dir.Combine("mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "sodium.jar"), "");
        File.WriteAllText(Path.Combine(mods, "Iris.jar.disabled"), "");
        File.WriteAllText(Path.Combine(mods, "notas.txt"), "");

        var list = InstanceMods.List(mods);

        list.ShouldBe([
            new ModFile("Iris.jar.disabled", "Iris.jar", Enabled: false),
            new ModFile("sodium.jar", "sodium.jar", Enabled: true),
        ]);
        InstanceMods.List(dir.Combine("no-existe")).ShouldBeEmpty();
    }

    [Fact]
    public void Disabling_and_enabling_renames_the_file()
    {
        using var dir = new TempDir();
        var mods = dir.Combine("mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "sodium.jar"), "contenido");
        var mod = InstanceMods.List(mods).Single();

        var off = InstanceMods.SetEnabled(mods, mod, enabled: false);
        off.ShouldBe(new ModFile("sodium.jar.disabled", "sodium.jar", Enabled: false));
        File.Exists(Path.Combine(mods, "sodium.jar")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(mods, "sodium.jar.disabled")).ShouldBe("contenido");

        var on = InstanceMods.SetEnabled(mods, off, enabled: true);
        on.ShouldBe(new ModFile("sodium.jar", "sodium.jar", Enabled: true));
        File.Exists(Path.Combine(mods, "sodium.jar")).ShouldBeTrue();
    }

    [Fact]
    public void Toggling_never_overwrites_the_same_mod_in_the_other_state()
    {
        using var dir = new TempDir();
        var mods = dir.Combine("mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "a.jar"), "nuevo");
        File.WriteAllText(Path.Combine(mods, "a.jar.disabled"), "viejo");

        Should.Throw<IOException>(() => InstanceMods.SetEnabled(mods, new ModFile("a.jar", "a.jar", true), enabled: false));
        File.ReadAllText(Path.Combine(mods, "a.jar.disabled")).ShouldBe("viejo");
    }

    [Fact]
    public void Names_with_paths_are_rejected()
    {
        using var dir = new TempDir();
        Should.Throw<ArgumentException>(() =>
            InstanceMods.SetEnabled(dir.Path, new ModFile("../x.jar", "../x.jar", true), enabled: false));
    }

    [Fact]
    public void Shaders_and_resource_packs_list_zips_and_folders()
    {
        using var dir = new TempDir();
        var packs = dir.Combine("resourcepacks");
        Directory.CreateDirectory(Path.Combine(packs, "Mi pack"));
        File.WriteAllBytes(Path.Combine(packs, "Mi pack", "pack.png"), Png);
        File.WriteAllText(Path.Combine(packs, "faithful.zip"), "");
        File.WriteAllText(Path.Combine(packs, "viejo.zip.disabled"), "");
        File.WriteAllText(Path.Combine(packs, "mod.jar"), "");

        var list = InstanceMods.List(packs, InstanceContentKind.ResourcePacks);

        list.ShouldBe([
            new ModFile("faithful.zip", "faithful.zip", Enabled: true),
            new ModFile("Mi pack", "Mi pack", Enabled: true, IsFolder: true),
            new ModFile("viejo.zip.disabled", "viejo.zip", Enabled: false),
        ]);
        InstanceMods.ReadIcon(Path.Combine(packs, "Mi pack")).ShouldBe(Png);
        Should.Throw<ArgumentException>(() => InstanceMods.SetEnabled(packs, list[1], enabled: false));
        InstanceMods.FolderName(InstanceContentKind.Shaders).ShouldBe("shaderpacks");
    }

    [Theory]
    [InlineData("fabric.mod.json", """{ "id": "x", "icon": "assets/x/icon.png" }""", "assets/x/icon.png")]
    [InlineData("fabric.mod.json", """{ "id": "x", "icon": { "16": "s.png", "128": "assets/big.png", "512": "huge.png" } }""", "assets/big.png")]
    [InlineData("quilt.mod.json", """{ "quilt_loader": { "metadata": { "icon": "q.png" } } }""", "q.png")]
    [InlineData("META-INF/mods.toml", "modLoader=\"javafml\"\nlogoFile=\"logo.png\"\n", "logo.png")]
    [InlineData("META-INF/neoforge.mods.toml", "logoFile = 'neo.png'\n", "neo.png")]
    public void The_icon_comes_from_the_mod_metadata(string metadataFile, string metadata, string iconPath)
    {
        using var dir = new TempDir();
        var jar = Jar(dir, (metadataFile, System.Text.Encoding.UTF8.GetBytes(metadata)), (iconPath, Png), ("pack.png", [9, 9]));

        InstanceMods.ReadIcon(jar).ShouldBe(Png);
    }

    [Fact]
    public void Without_declared_icon_pack_png_is_used_and_otherwise_none()
    {
        using var dir = new TempDir();
        InstanceMods.ReadIcon(Jar(dir, ("pack.png", Png))).ShouldBe(Png);
        InstanceMods.ReadIcon(Jar(dir, ("fabric.mod.json", "{\"id\":\"x\"}"u8.ToArray()))).ShouldBeNull();

        var notAJar = dir.Combine("roto.jar");
        File.WriteAllText(notAJar, "no es un zip");
        InstanceMods.ReadIcon(notAJar).ShouldBeNull();
    }

    private static string Jar(TempDir dir, params (string Name, byte[] Data)[] entries)
    {
        var path = dir.Combine(Guid.NewGuid().ToString("N") + ".jar");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        return path;
    }
}
