using System.IO.Compression;
using System.Text;
using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Importar un servidor existente: reconocer qué es por sus archivos y copiarlo sin lo que se regenera.</summary>
public class ServerImporterTests
{
    [Fact]
    public void Vanilla_is_recognised_by_the_version_inside_server_jar()
    {
        using var dir = new TempDir();
        Jar(dir.Combine("server.jar"), ("version.json", """{"id":"1.21.1","name":"1.21.1"}"""));

        ServerImporter.Detect(dir.Path).ShouldBe(new ImportedServer(ServerSoftware.Vanilla, "1.21.1", null, ["-jar", "server.jar"]), new ImportedComparer());
    }

    [Fact]
    public void Fabric_reads_both_versions_from_its_launcher()
    {
        using var dir = new TempDir();
        Jar(dir.Combine("fabric-server-launch.jar"), ("install.properties", "fabric-loader-version=0.16.5\ngame-version=1.21.1\n"));

        var server = ServerImporter.Detect(dir.Path).ShouldNotBeNull();
        server.Software.ShouldBe(ServerSoftware.Fabric);
        server.MinecraftVersion.ShouldBe("1.21.1");
        server.LoaderVersion.ShouldBe("0.16.5");
        server.LaunchArguments.ShouldBe(["-jar", "fabric-server-launch.jar"]);
    }

    [Fact]
    public void Forge_and_neoforge_are_recognised_by_their_libraries()
    {
        using var forge = new TempDir();
        File.WriteAllText(Dir(forge.Combine("libraries", "net", "minecraftforge", "forge", "1.20.1-47.3.0"), "win_args.txt"), "");
        File.WriteAllText(forge.Combine("user_jvm_args.txt"), "");
        var f = ServerImporter.Detect(forge.Path).ShouldNotBeNull();
        f.Software.ShouldBe(ServerSoftware.Forge);
        (f.MinecraftVersion, f.LoaderVersion).ShouldBe(("1.20.1", "47.3.0"));
        f.LaunchArguments.ShouldBe(["@user_jvm_args.txt", "@libraries/net/minecraftforge/forge/1.20.1-47.3.0/win_args.txt"]);

        using var neo = new TempDir();
        File.WriteAllText(Dir(neo.Combine("libraries", "net", "neoforged", "neoforge", "21.1.77"), "win_args.txt"), "");
        var n = ServerImporter.Detect(neo.Path).ShouldNotBeNull();
        n.Software.ShouldBe(ServerSoftware.NeoForge);
        (n.MinecraftVersion, n.LoaderVersion).ShouldBe(("1.21.1", "21.1.77")); // sin librerías del juego: por la versión de NeoForge
    }

    [Theory]
    [InlineData("paper-1.21.1-123.jar", ServerSoftware.Paper, "1.21.1", "123")]
    [InlineData("purpur-1.21.1-2329.jar", ServerSoftware.Purpur, "1.21.1", "2329")]
    [InlineData("spigot-1.21.1.jar", ServerSoftware.Spigot, "1.21.1", null)]
    public void Plugin_servers_are_recognised_by_the_jar_name(string jar, ServerSoftware software, string minecraft, string? build)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(dir.Combine(jar), "");

        var server = ServerImporter.Detect(dir.Path).ShouldNotBeNull();
        server.Software.ShouldBe(software);
        (server.MinecraftVersion, server.LoaderVersion).ShouldBe((minecraft, build));
        server.LaunchArguments.ShouldBe(["-jar", jar]);
    }

    [Fact]
    public void A_renamed_paper_jar_is_recognised_by_its_version_history()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(dir.Combine("server.jar"), "");
        File.WriteAllText(dir.Combine("version_history.json"), """{"currentVersion":"git-Paper-196 (MC: 1.21.1)"}""");

        var server = ServerImporter.Detect(dir.Path).ShouldNotBeNull();
        server.Software.ShouldBe(ServerSoftware.Paper);
        (server.MinecraftVersion, server.LoaderVersion).ShouldBe(("1.21.1", "196"));
        server.LaunchArguments.ShouldBe(["-jar", "server.jar"]);
    }

    [Fact]
    public void A_folder_without_a_server_is_not_recognised()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("world"));
        File.WriteAllText(dir.Combine("notas.txt"), "");

        ServerImporter.Detect(dir.Path).ShouldBeNull();
        ServerImporter.Detect(dir.Combine("no-existe")).ShouldBeNull();
    }

    [Fact]
    public async Task Copy_keeps_the_world_and_mods_but_not_logs()
    {
        using var source = new TempDir();
        File.WriteAllText(Dir(source.Combine("world"), "level.dat"), "mundo");
        File.WriteAllText(Dir(source.Combine("mods"), "sodium.jar"), "mod");
        File.WriteAllText(source.Combine("server.properties"), "motd=Hola");
        File.WriteAllText(Dir(source.Combine("logs"), "latest.log"), new string('x', 5000));
        using var target = new TempDir();

        ServerImporter.CopySize(source.Path).ShouldBe("mundo".Length + "mod".Length + "motd=Hola".Length);
        await ServerImporter.CopyAsync(source.Path, target.Path, null, TestContext.Current.CancellationToken);

        File.ReadAllText(target.Combine("world", "level.dat")).ShouldBe("mundo");
        File.ReadAllText(target.Combine("mods", "sodium.jar")).ShouldBe("mod");
        File.ReadAllText(target.Combine("server.properties")).ShouldBe("motd=Hola");
        Directory.Exists(target.Combine("logs")).ShouldBeFalse();
        File.Exists(source.Combine("logs", "latest.log")).ShouldBeTrue(); // el original no se toca
    }

    /// <summary>Crea la carpeta y devuelve la ruta de <paramref name="file"/> dentro de ella.</summary>
    private static string Dir(string path, string file)
    {
        Directory.CreateDirectory(path);
        return Path.Combine(path, file);
    }

    private static void Jar(string path, params (string Name, string Text)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <summary>Compara también la lista de argumentos por contenido (el record compara la referencia de la lista).</summary>
    private sealed class ImportedComparer : IEqualityComparer<ImportedServer?>
    {
        public bool Equals(ImportedServer? a, ImportedServer? b) =>
            a is not null && b is not null && a.Software == b.Software && a.MinecraftVersion == b.MinecraftVersion
            && a.LoaderVersion == b.LoaderVersion && a.LaunchArguments.SequenceEqual(b.LaunchArguments);

        public int GetHashCode(ImportedServer? obj) => 0;
    }
}
