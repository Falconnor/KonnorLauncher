using System.Security.Cryptography;
using KonnorLauncher.Core.Verification;

namespace KonnorLauncher.Core.Hosting;

/// <summary>
/// «Paquetes de recursos obligatorios» también en el propio Minecraft: el servidor envía su paquete de
/// recursos al entrar y expulsa a quien lo rechace (<c>resource-pack</c>, <c>resource-pack-sha1</c> y
/// <c>require-resource-pack</c> de <c>server.properties</c>). El paquete es el primer <c>.zip</c> de
/// <c>cliente/resourcepacks</c> y lo descarga el juego de la propia sincronización (<c>/client/...</c>).
/// Solo se toca el <c>resource-pack</c> que puso el launcher: uno configurado a mano se respeta.
/// </summary>
public static class ServerResourcePack
{
    /// <summary>Paquete que se envía: el primero (por nombre) de <c>resourcepacks</c> de la carpeta del cliente.</summary>
    public static string? FindPack(string clientDir)
    {
        var dir = Path.Combine(clientDir, "resourcepacks");
        if (!Directory.Exists(dir))
            return null;
        return Directory.EnumerateFiles(dir, "*.zip", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// Aplica la política a <c>server.properties</c> y devuelve un texto para el jugador (null si no hay nada
    /// que decir). <paramref name="baseUrl"/> es la dirección de la sincronización (<c>http://host:puerto</c>).
    /// Actualiza <see cref="ClientPublishSettings.ManagedResourcePackUrl"/>.
    /// </summary>
    public static string? Apply(HostedServer server, string serverDir, string clientDir, string baseUrl)
    {
        var path = Path.Combine(serverDir, ServerPropertiesFile.FileName);
        var props = ServerPropertiesFile.Load(path);
        var publish = server.Publish;
        var current = props.Get("resource-pack", string.Empty);
        var ours = publish.ManagedResourcePackUrl is { Length: > 0 } managed && current == managed;
        var wanted = publish.Enabled && publish.ProtectPacks;
        string? message = null;

        if (wanted && (current.Length == 0 || ours))
        {
            if (FindPack(clientDir) is { } pack)
            {
                var relative = "resourcepacks/" + Path.GetFileName(pack);
                var url = $"{baseUrl.TrimEnd('/')}/client/{ManifestPath.EncodeUrlPath(relative)}";
                props.Set("resource-pack", url);
                props.Set("resource-pack-sha1", Sha1(pack));
                props.Set("require-resource-pack", true);
                publish.ManagedResourcePackUrl = url;
                props.Save(path);
                return $"El servidor exige el paquete {Path.GetFileName(pack)} al entrar.";
            }

            message = "Para exigirlo también en el juego, pon un paquete .zip en la carpeta resourcepacks del cliente.";
        }
        else if (wanted)
        {
            return "server.properties ya tiene otro paquete de recursos (puesto a mano): se respeta.";
        }

        // Ya no se exige (o no hay paquete): se quita solo lo que puso el launcher.
        if (ours)
        {
            props.Set("resource-pack", string.Empty);
            props.Set("resource-pack-sha1", string.Empty);
            props.Set("require-resource-pack", false);
            props.Save(path);
        }

        publish.ManagedResourcePackUrl = null;
        return message;
    }

    private static string Sha1(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA1.HashData(stream));
    }
}
