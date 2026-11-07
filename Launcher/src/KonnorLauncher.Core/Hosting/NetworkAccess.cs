using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Hosting;

/// <summary>
/// Cómo llegan los jugadores a este equipo:
/// <list type="bullet">
/// <item>IP pública (la consulta a <c>api.ipify.org</c>, como hacía el panel de LauncherBackEnd).</item>
/// <item>IP de la red local, para jugar en la misma casa.</item>
/// <item>IP del router (puerta de enlace), para abrir su página y redirigir los puertos: cada router es
/// distinto y pide su contraseña, así que eso lo hace el jugador.</item>
/// </list>
/// </summary>
public sealed class NetworkAccess
{
    public const string HttpClientName = "ipify";
    public const string PublicIpUrl = "https://api.ipify.org";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;
    private string? _publicIp;

    public NetworkAccess(IHttpClientFactory httpFactory, ILogger<NetworkAccess>? logger = null)
    {
        _httpFactory = httpFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>IP pública de la red (null sin conexión). Se guarda tras la primera consulta.</summary>
    public async Task<string?> GetPublicIpAsync(CancellationToken ct = default)
    {
        if (_publicIp is not null)
            return _publicIp;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var text = (await _httpFactory.CreateClient(HttpClientName).GetStringAsync(PublicIpUrl, timeout.Token).ConfigureAwait(false)).Trim();
            return _publicIp = IPAddress.TryParse(text, out _) ? text : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogInformation(ex, "No se pudo consultar la IP pública");
            return null;
        }
    }

    /// <summary>
    /// Dirección con la que llegan los jugadores: la configurada (un dominio o IP fija), si no la IP pública
    /// y, sin conexión, la de la red local.
    /// </summary>
    public async Task<string> GetPlayerHostAsync(string? configured, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(configured) ? configured.Trim()
        : await GetPublicIpAsync(ct).ConfigureAwait(false) ?? GetLanIp() ?? "localhost";

    /// <summary>IPv4 de este equipo en la red local (la de la interfaz que tiene router).</summary>
    public static string? GetLanIp() => FindLanInterface()?.Address;

    /// <summary>IPv4 del router (la puerta de enlace de la red local).</summary>
    public static string? GetGatewayIp() => FindLanInterface()?.Gateway;

    private static (string Address, string Gateway)? FindLanInterface()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            var props = nic.GetIPProperties();
            var gateway = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            var address = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            if (gateway is not null && address is not null)
                return (address.ToString(), gateway.ToString());
        }

        return null;
    }

    /// <summary>El puerto TCP está libre en este equipo (nadie escucha en él).</summary>
    public static bool IsPortFree(int port)
    {
        try
        {
            return !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }
}
