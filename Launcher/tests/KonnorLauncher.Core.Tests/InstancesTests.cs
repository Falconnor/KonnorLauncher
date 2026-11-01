using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Loaders;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

public sealed class InstancesTests
{
    // ── InstanceStore ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Survival con amigos", "survival-con-amigos")]
    [InlineData("  Técnico Ñandú 1.21  ", "tecnico-nandu-1-21")]
    [InlineData("!!!", "instancia")]
    [InlineData("Un nombre muy largo que supera el límite de treinta y dos", "un-nombre-muy-largo-que-supera-e")]
    public void Slugify_makes_safe_folder_names(string name, string expected) =>
        InstanceStore.Slugify(name).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678901234567890123456789012345678901")]
    public void Invalid_names_are_rejected(string name) =>
        Should.Throw<ArgumentException>(() => InstanceStore.ValidateName(name));

    [Fact]
    public void Create_assigns_unique_ids_and_creates_the_folder()
    {
        using var dir = new TempDir();
        var store = new InstanceStore(dir.Path);

        var a = store.Create(new GameInstance { Name = "Mi mundo", MinecraftVersion = "1.21.1" });
        var b = store.Create(new GameInstance { Name = "Mi  mundo", MinecraftVersion = "1.20.1" });

        a.Id.ShouldBe("mi-mundo");
        b.Id.ShouldBe("mi-mundo-2");
        b.Name.ShouldBe("Mi mundo"); // espacios repetidos normalizados
        Directory.Exists(Path.Combine(dir.Path, "mi-mundo", "mods")).ShouldBeTrue();
        File.Exists(Path.Combine(dir.Path, "mi-mundo-2", InstanceStore.FileName)).ShouldBeTrue();
    }

    [Fact]
    public void Save_and_load_round_trip_every_field()
    {
        using var dir = new TempDir();
        var store = new InstanceStore(dir.Path);
        var created = store.Create(new GameInstance
        {
            Name = "Fabric",
            MinecraftVersion = "1.21.1",
            Loader = ModLoader.Fabric,
            LoaderChannel = LoaderChannel.Specific,
            LoaderVersion = "0.16.10",
            SyncWithServer = true,
            ServerUrl = "http://127.0.0.1:59381",
        });
        created.LastPlayed = DateTimeOffset.Now;
        created.InstalledVersionId = "fabric-loader-0.16.10-1.21.1";
        store.Save(created);

        var loaded = store.LoadAll().ShouldHaveSingleItem();
        loaded.Id.ShouldBe(created.Id);
        loaded.Loader.ShouldBe(ModLoader.Fabric);
        loaded.LoaderChannel.ShouldBe(LoaderChannel.Specific);
        loaded.LoaderVersion.ShouldBe("0.16.10");
        loaded.SyncWithServer.ShouldBeTrue();
        loaded.InstalledVersionId.ShouldBe("fabric-loader-0.16.10-1.21.1");
        loaded.Summary.ShouldBe("1.21.1 · Fabric 0.16.10");
        File.ReadAllText(Path.Combine(dir.Path, created.Id, InstanceStore.FileName)).ShouldContain("\"loader\": \"Fabric\"");
    }

    [Fact]
    public void LoadAll_orders_by_last_played_and_skips_damaged_files()
    {
        using var dir = new TempDir();
        var store = new InstanceStore(dir.Path);
        var old = store.Create(new GameInstance { Name = "Vieja", MinecraftVersion = "1.20.1" });
        var recent = store.Create(new GameInstance { Name = "Reciente", MinecraftVersion = "1.21.1" });
        recent.LastPlayed = DateTimeOffset.Now;
        store.Save(recent);
        Directory.CreateDirectory(Path.Combine(dir.Path, "rota"));
        File.WriteAllText(Path.Combine(dir.Path, "rota", InstanceStore.FileName), "{ no es json");

        store.LoadAll().Select(i => i.Id).ShouldBe([recent.Id, old.Id]);
    }

    [Fact]
    public void Delete_removes_the_folder_and_rejects_ids_outside_the_store()
    {
        using var dir = new TempDir();
        var store = new InstanceStore(Path.Combine(dir.Path, "instances"));
        var instance = store.Create(new GameInstance { Name = "Borrar", MinecraftVersion = "1.21.1" });
        File.WriteAllText(Path.Combine(store.GetDirectory(instance.Id), "mods", "a.jar"), "x");

        store.Delete(instance.Id);

        Directory.Exists(store.GetDirectory(instance.Id)).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => store.Delete(".."));
        Directory.Exists(dir.Path).ShouldBeTrue();
    }

    [Fact]
    public void Legacy_properties_become_the_first_instance_only_once()
    {
        using var dir = new TempDir();
        var props = dir.Combine("launcher.properties");
        File.WriteAllText(props, "minecraft.version=1.20.1-forge-47.3.0\nserver.url=http://10.0.0.5:59381\nenable.verify=true\ndelete_unauthorized=true\ncached.client.version=1.0.1\n");
        var settings = new LauncherSettings(new LauncherProperties(props));
        var store = new InstanceStore(dir.Combine("instances"));

        var migrated = store.MigrateLegacy(settings).ShouldNotBeNull();
        migrated.Name.ShouldBe("Konnor");
        migrated.MinecraftVersion.ShouldBe("1.20.1-forge-47.3.0");
        migrated.Loader.ShouldBe(ModLoader.Vanilla);
        migrated.SyncWithServer.ShouldBeTrue();
        migrated.ServerUrl.ShouldBe("http://10.0.0.5:59381");
        migrated.ServerClientVersion.ShouldBe("1.0.1");

        store.MigrateLegacy(settings).ShouldBeNull(); // ya hay instancias
        store.LoadAll().Count.ShouldBe(1);
    }

    [Fact]
    public void Fresh_install_without_version_or_verification_creates_nothing()
    {
        using var dir = new TempDir();
        var props = dir.Combine("launcher.properties");
        File.WriteAllText(props, "minecraft.version=\nenable.verify=false\n");
        var store = new InstanceStore(dir.Combine("instances"));

        store.MigrateLegacy(new LauncherSettings(new LauncherProperties(props))).ShouldBeNull();
    }

    // ── Ids de versión de los cargadores ─────────────────────────────────

    [Theory]
    [InlineData("fabric-loader-0.19.3-1.21.1", "1.21.1", ModLoader.Fabric, "0.19.3")]
    [InlineData("quilt-loader-0.20.0-beta.9-1.20.1", "1.20.1", ModLoader.Quilt, "0.20.0-beta.9")]
    [InlineData("1.20.1-forge-47.3.0", "1.20.1", ModLoader.Forge, "47.3.0")]
    [InlineData("1.12.2-forge1.12.2-14.23.5.2859", "1.12.2", ModLoader.Forge, "14.23.5.2859")]
    [InlineData("neoforge-21.1.77", "1.21.1", ModLoader.NeoForge, "21.1.77")]
    [InlineData("neoforge-20.4.237", "1.20.4", ModLoader.NeoForge, "20.4.237")]
    [InlineData("1.21.1", "1.21.1", ModLoader.Vanilla, null)]
    [InlineData("26.3", "26.3", ModLoader.Vanilla, null)]
    public void Loader_version_ids_are_understood(string id, string minecraft, ModLoader loader, string? version) =>
        GameInstance.ParseVersionId(id).ShouldBe((minecraft, loader, version));

    [Fact]
    public void Synced_instance_summary_shows_the_server_loader()
    {
        var instance = new GameInstance { MinecraftVersion = "fabric-loader-0.19.3-1.21.1", SyncWithServer = true };
        instance.Summary.ShouldBe("1.21.1 · Fabric 0.19.3");
    }

    // ── Memoria por instancia ────────────────────────────────────────────

    [Theory]
    [InlineData("6", 6144)]
    [InlineData("6 GB", 6144)]
    [InlineData("6,5", 6656)]
    [InlineData("6.5g", 6656)]
    [InlineData("6144", 6144)]
    [InlineData("6144 MB", 6144)]
    [InlineData("512m", 512)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData("-2", null)]
    public void Memory_text_is_understood(string text, int? expected) =>
        SystemMemory.Parse(text).ShouldBe(expected);

    [Fact]
    public void Memory_is_formatted_and_clamped()
    {
        SystemMemory.Format(4096).ShouldBe("4 GB");
        SystemMemory.Format(6656).ShouldBe("6,5 GB");
        SystemMemory.Clamp(100).ShouldBe(SystemMemory.MinMb);
        SystemMemory.Clamp(6700).ShouldBe(6656); // al paso de 512 MB
        SystemMemory.Clamp(10_000_000).ShouldBe(SystemMemory.MaxMb);
    }

    [Fact]
    public void Instance_memory_is_saved_and_null_means_the_global_setting()
    {
        using var dir = new TempDir();
        var store = new InstanceStore(dir.Path);
        var custom = store.Create(new GameInstance { Name = "Con RAM", MinecraftVersion = "1.21.1", MemoryMb = 6144 });
        store.Create(new GameInstance { Name = "Sin RAM", MinecraftVersion = "1.21.1" });

        var loaded = store.LoadAll();
        loaded.Single(i => i.Id == custom.Id).MemoryMb.ShouldBe(6144);
        loaded.Single(i => i.Id != custom.Id).MemoryMb.ShouldBeNull();
    }

    // ── InstanceLayout ───────────────────────────────────────────────────

    [Fact]
    public void Normal_instances_share_meta_and_synced_ones_keep_their_own_files()
    {
        var normal = InstanceLayout.For(new GameInstance { Id = "a" }, @"C:\d\instances", @"C:\d\meta");
        normal.GameDir.ShouldBe(@"C:\d\instances\a");
        normal.VersionsDir.ShouldBe(@"C:\d\meta\versions");
        normal.LibrariesDir.ShouldBe(@"C:\d\meta\libraries");
        normal.AssetsDir.ShouldBe(@"C:\d\meta\assets");
        normal.RuntimeDir.ShouldBe(@"C:\d\meta\runtime");

        // La verificación borra lo que no está en el manifest: nunca debe tocar lo compartido.
        var synced = InstanceLayout.For(new GameInstance { Id = "s", SyncWithServer = true }, @"C:\d\instances", @"C:\d\meta");
        synced.VersionsDir.ShouldBe(@"C:\d\instances\s\versions");
        synced.LibrariesDir.ShouldBe(@"C:\d\instances\s\libraries");
        synced.AssetsDir.ShouldBe(@"C:\d\instances\s\assets");
        synced.RuntimeDir.ShouldBe(@"C:\d\meta\runtime"); // el Java sí se comparte
    }

    // ── InstanceSync: versión que publica el servidor ────────────────────

    private static void WriteVersion(TempDir dir, string id, string json) =>
        dir.Write($"versions/{id}/{id}.json", json);

    [Fact]
    public void Server_version_prefers_the_loader_over_vanilla()
    {
        using var dir = new TempDir();
        WriteVersion(dir, "1.20.1", """{ "id": "1.20.1", "releaseTime": "2023-06-12T00:00:00+00:00" }""");
        WriteVersion(dir, "1.20.1-forge-47.3.0", """{ "id": "1.20.1-forge-47.3.0", "inheritsFrom": "1.20.1" }""");

        InstanceSync.DetectVersion(dir.Combine("versions")).ShouldBe("1.20.1-forge-47.3.0");
    }

    [Fact]
    public void Standalone_loader_json_is_detected_by_its_main_class()
    {
        // JSON completo de Fabric (sin inheritsFrom), como el del cliente de pruebas del servidor.
        using var dir = new TempDir();
        WriteVersion(dir, "1.21.1", """{ "id": "1.21.1", "mainClass": "net.minecraft.client.main.Main", "releaseTime": "2024-08-08T12:24:45+00:00" }""");
        WriteVersion(dir, "fabric-loader-0.19.3-1.21.1", """{ "id": "fabric-loader-0.19.3-1.21.1", "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient", "releaseTime": "2024-08-08T12:24:45-04:00" }""");

        InstanceSync.DetectVersion(dir.Combine("versions")).ShouldBe("fabric-loader-0.19.3-1.21.1");
    }

    [Fact]
    public void Server_version_without_loader_is_the_newest_vanilla()
    {
        using var dir = new TempDir();
        WriteVersion(dir, "1.20.1", """{ "id": "1.20.1", "releaseTime": "2023-06-12T00:00:00+00:00" }""");
        WriteVersion(dir, "1.21.1", """{ "id": "1.21.1", "releaseTime": "2024-08-08T00:00:00+00:00" }""");
        dir.Write("versions/vacia/otro.txt", "sin json"); // carpetas sin <id>.json no cuentan

        InstanceSync.DetectVersion(dir.Combine("versions")).ShouldBe("1.21.1");
    }

    [Fact]
    public void Server_without_versions_has_no_version()
    {
        using var dir = new TempDir();
        InstanceSync.DetectVersion(dir.Combine("versions")).ShouldBeNull();
        WriteVersion(dir, "rota", "{ no es json");
        InstanceSync.DetectVersion(dir.Combine("versions")).ShouldBeNull();
    }

    // ── LoaderVersionService ─────────────────────────────────────────────

    [Fact]
    public void Fabric_list_uses_the_stable_flag()
    {
        const string json = """
            [ { "loader": { "version": "0.17.0", "stable": false } },
              { "loader": { "version": "0.16.10", "stable": true } },
              { "loader": { "version": "0.16.9", "stable": true } } ]
            """;
        var list = LoaderVersionService.ParseFabricLike(json, quilt: false);

        list.Select(v => v.Version).ShouldBe(["0.17.0", "0.16.10", "0.16.9"]);
        LoaderVersionService.Pick(list, LoaderChannel.Stable).ShouldBe("0.16.10");
        LoaderVersionService.Pick(list, LoaderChannel.Latest).ShouldBe("0.17.0");
    }

    [Fact]
    public void Quilt_betas_are_not_stable()
    {
        const string json = """
            [ { "loader": { "version": "0.20.0-beta.9" } },
              { "loader": { "version": "0.19.2" } } ]
            """;
        var list = LoaderVersionService.ParseFabricLike(json, quilt: true);

        LoaderVersionService.Pick(list, LoaderChannel.Stable).ShouldBe("0.19.2");
        LoaderVersionService.Pick(list, LoaderChannel.Latest).ShouldBe("0.20.0-beta.9");
    }

    [Fact]
    public void Without_stable_versions_stable_falls_back_to_the_newest()
    {
        var list = new[] { new LoaderVersionInfo("21.4.0-beta", false), new LoaderVersionInfo("21.3.9-beta", false) };
        LoaderVersionService.Pick(list, LoaderChannel.Stable).ShouldBe("21.4.0-beta");
        LoaderVersionService.Pick([], LoaderChannel.Stable).ShouldBeNull();
    }

    [Fact]
    public void Empty_or_unexpected_json_gives_an_empty_list()
    {
        LoaderVersionService.ParseFabricLike("", quilt: false).ShouldBeEmpty();
        LoaderVersionService.ParseFabricLike("{}", quilt: false).ShouldBeEmpty();
        LoaderVersionService.ParseFabricLike("[{\"other\":1}]", quilt: true).ShouldBeEmpty();
    }
}
