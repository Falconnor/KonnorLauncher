using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace KonnorLauncher.Core.Servers;

/// <summary>Calidad del ping: buena hasta 80 ms, regular hasta 120 ms y mala por encima.</summary>
public enum PingQuality
{
    Good,
    Fair,
    Poor,
}

/// <summary>
/// Ping desde este equipo hasta el servidor: lo que tarda en abrirse la conexión TCP (una ida y vuelta),
/// sin contar la búsqueda DNS. No envía nada al servidor de Minecraft: abre la conexión y la cierra.
/// Los servidores que solo publican su puerto con un registro SRV pueden no responder en el 25565;
/// entonces no hay ping (null).
/// </summary>
public static class ServerPing
{
    public const int GoodMaxMs = 80;
    public const int FairMaxMs = 120;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public static PingQuality Quality(int milliseconds) => milliseconds switch
    {
        <= GoodMaxMs => PingQuality.Good,
        <= FairMaxMs => PingQuality.Fair,
        _ => PingQuality.Poor,
    };

    /// <summary>Milisegundos de la conexión; null si no se pudo conectar a tiempo.</summary>
    public static async Task<int?> MeasureAsync(ServerAddress address, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            var ips = IPAddress.TryParse(address.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(address.Host, limit.Token).ConfigureAwait(false);
            var ip = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? ips.FirstOrDefault();
            if (ip is null)
                return null;

            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var watch = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(ip, address.Port), limit.Token).ConfigureAwait(false);
            watch.Stop();
            return Math.Max(1, (int)Math.Round(watch.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return null;
        }
    }
}
