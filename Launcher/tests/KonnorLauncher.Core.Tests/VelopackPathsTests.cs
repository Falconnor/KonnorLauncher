using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>
/// Instalado con Velopack, la carpeta del programa se reemplaza en cada actualización: la configuración tiene que vivir
/// en la carpeta de datos.
/// </summary>
public class VelopackPathsTests
{
    [Fact]
    public void Outside_velopack_the_properties_stay_next_to_the_exe()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);

        LauncherPaths.IsVelopackInstall(dir.Path).ShouldBeFalse();
        new LauncherPaths(dir.Path, dir.Combine("data")).PropertiesPath.ShouldBe(Path.Combine(dir.Path, "launcher.properties"));
    }

    [Fact]
    public void A_folder_called_current_without_update_exe_is_not_velopack()
    {
        using var dir = new TempDir();
        var current = dir.Combine("current");
        Directory.CreateDirectory(current);

        LauncherPaths.IsVelopackInstall(current).ShouldBeFalse();
    }

    [Fact]
    public void Installed_with_velopack_the_properties_go_to_the_data_folder_and_are_seeded_once()
    {
        using var dir = new TempDir();
        var current = dir.Combine("KonnorLauncher", "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(dir.Combine("KonnorLauncher", "Update.exe"), "");
        File.WriteAllText(Path.Combine(current, "launcher.properties"), "java.memory=4G\n");
        var data = dir.Combine("data");
        var paths = new LauncherPaths(current, data);

        LauncherPaths.IsVelopackInstall(current).ShouldBeTrue();
        paths.PropertiesPath.ShouldBe(Path.Combine(data, "launcher.properties"));

        paths.EnsurePropertiesFile();
        File.ReadAllText(paths.PropertiesPath).ShouldBe("java.memory=4G\n");

        // La del jugador nunca se pisa con la que trae una versión nueva.
        File.WriteAllText(paths.PropertiesPath, "player.username=Steve\n");
        paths.EnsurePropertiesFile();
        File.ReadAllText(paths.PropertiesPath).ShouldBe("player.username=Steve\n");
    }
}
