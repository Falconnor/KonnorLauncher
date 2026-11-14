using System.Net;
using System.Net.Sockets;
using System.Text;
using KonnorLauncher.Core.Servers;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Ping directo a un servidor de Minecraft (el protocolo de la lista de servidores del juego).</summary>
public class ServerPingerTests
{
    [Fact]
    public async Task A_running_server_answers_with_its_players_and_version()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = client.GetStream();
            await ReadPacketAsync(stream); // handshake
            await ReadPacketAsync(stream); // status request
            var json = Encoding.UTF8.GetBytes("""{"version":{"name":"Paper 1.21.1","protocol":767},"players":{"max":20,"online":3},"description":{"text":"Hola"}}""");
            var body = new MemoryStream();
            ServerPinger.WriteVarInt(body, 0x00);
            ServerPinger.WriteVarInt(body, json.Length);
            body.Write(json);
            var packet = new MemoryStream();
            ServerPinger.WriteVarInt(packet, (int)body.Length);
            packet.Write(body.ToArray());
            await stream.WriteAsync(packet.ToArray(), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var status = await ServerPinger.PingAsync(new ServerAddress("127.0.0.1", port), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await server;
        listener.Stop();

        status.ShouldNotBeNull();
        status.Online.ShouldBeTrue();
        (status.PlayersOnline, status.PlayersMax).ShouldBe((3, 20));
        status.Version.ShouldBe("Paper 1.21.1");
        status.Motd.ShouldBe("Hola");
    }

    [Fact]
    public async Task A_closed_port_means_off()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nadie escucha: como un servidor apagado

        (await ServerPinger.PingAsync(new ServerAddress("127.0.0.1", port), TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    private static async Task ReadPacketAsync(NetworkStream stream)
    {
        int length = 0, shift = 0;
        var one = new byte[1];
        do
        {
            await stream.ReadExactlyAsync(one, TestContext.Current.CancellationToken);
            length |= (one[0] & 0x7F) << shift;
            shift += 7;
        }
        while ((one[0] & 0x80) != 0);
        await stream.ReadExactlyAsync(new byte[length], TestContext.Current.CancellationToken);
    }
}
