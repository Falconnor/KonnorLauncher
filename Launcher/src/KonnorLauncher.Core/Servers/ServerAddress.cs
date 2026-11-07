using System.Globalization;
using System.Text.RegularExpressions;

namespace KonnorLauncher.Core.Servers;

/// <summary>
/// Dirección de un servidor de Minecraft: <c>host</c> o <c>host:puerto</c> (puerto 25565 si no se indica).
/// Solo se aceptan nombres de dominio e IPv4: es lo que se pasa al juego y a las consultas de estado,
/// así que nada de espacios, rutas ni esquemas (<c>http://</c>).
/// </summary>
public sealed partial record ServerAddress(string Host, int Port)
{
    public const int DefaultPort = 25565;

    /// <summary>Como la escribe el jugador: sin el puerto si es el de siempre.</summary>
    public override string ToString() => Port == DefaultPort ? Host : $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? text, out ServerAddress address)
    {
        address = null!;
        var value = text?.Trim() ?? string.Empty;
        if (value.Length is 0 or > 253 + 6)
            return false;
        var host = value;
        var port = DefaultPort;
        var colon = value.LastIndexOf(':');
        if (colon >= 0)
        {
            host = value[..colon];
            if (!int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
                return false;
        }

        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length is 0 or > 253 || !HostPattern().IsMatch(host))
            return false;
        address = new ServerAddress(host, port);
        return true;
    }

    /// <summary>Etiquetas de letras, números, guiones y guiones bajos separadas por puntos (también IPv4).</summary>
    [GeneratedRegex(@"^[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?(\.[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?)*$")]
    private static partial Regex HostPattern();
}
