using System.Net;
using System.Net.NetworkInformation;

namespace KonnorLauncher.Core.Hosting;

/// <summary>
/// Calidad de la conexión de un jugador. El servidor vanilla no da su ping por comando, así que se mide la latencia
/// hasta la IP desde la que se conectó (ICMP). Si su router no responde, no se sabe (<c>null</c>).
/// </summary>
public static class ConnectionQuality
{
    /// <summary>
    /// Barras como en la lista de jugadores del juego: &lt;150 ms = 5, &lt;300 = 4, &lt;600 = 3, &lt;1000 = 2, más = 1;
    /// 0 si no se sabe.
    /// </summary>
    public static int Bars(long? ms) => ms switch
    {
        null or < 0 => 0,
        < 150 => 5,
        < 300 => 4,
        < 600 => 3,
        < 1000 => 2,
        _ => 1,
    };

    /// <summary>Latencia en ms hasta <paramref name="address"/>; null si no es una IP o no responde a tiempo.</summary>
    public static async Task<long?> MeasureAsync(string? address, int timeoutMs = 1500)
    {
        if (address is null || !IPAddress.TryParse(address, out var ip))
            return null;
        if (IPAddress.IsLoopback(ip))
            return 0; // en este mismo equipo
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, timeoutMs);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return null;
        }
    }
}
