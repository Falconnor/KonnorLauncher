using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace KonnorLauncher.Core.Servers;

/// <summary>
/// «Server List Ping» de Minecraft (1.7 en adelante), el mismo que hace la lista de servidores del juego: se conecta por
/// TCP al servidor y le pide su estado. A diferencia de mcstatus.io llega también a servidores de la red local.
/// No resuelve registros SRV (las direcciones de algunos hostings): para esas está mcstatus.io.
/// </summary>
public static class ServerPinger
{
    private const int MaxResponseBytes = 1024 * 1024;

    /// <summary>Estado del servidor; null si no responde (apagado, inaccesible o no es un servidor de Minecraft).</summary>
    public static async Task<ServerStatus?> PingAsync(ServerAddress address, TimeSpan timeout, CancellationToken ct = default)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address.Host, address.Port, timer.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            // Handshake (id 0x00): protocolo -1 («cualquiera»), dirección, puerto y siguiente estado 1 (status).
            var handshake = new MemoryStream();
            WriteVarInt(handshake, 0x00);
            WriteVarInt(handshake, -1);
            WriteString(handshake, address.Host);
            handshake.WriteByte((byte)(address.Port >> 8));
            handshake.WriteByte((byte)address.Port);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), timer.Token).ConfigureAwait(false);
            // Status request (id 0x00, sin datos).
            await WritePacketAsync(stream, [0x00], timer.Token).ConfigureAwait(false);

            var length = await ReadVarIntAsync(stream, timer.Token).ConfigureAwait(false);
            if (length is <= 0 or > MaxResponseBytes)
                return null;
            var packet = new byte[length];
            await stream.ReadExactlyAsync(packet, timer.Token).ConfigureAwait(false);
            using var reader = new MemoryStream(packet);
            if (ReadVarInt(reader) != 0x00)
                return null;
            var jsonLength = ReadVarInt(reader);
            if (jsonLength <= 0 || jsonLength > reader.Length - reader.Position)
                return null;
            var json = Encoding.UTF8.GetString(packet, (int)reader.Position, jsonLength);
            return ParseStatus(json);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or JsonException
                                       or EndOfStreamException or InvalidDataException or ArgumentException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return null;
        }
    }

    /// <summary>La respuesta JSON del servidor: jugadores, versión y MOTD.</summary>
    internal static ServerStatus ParseStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int online = 0, max = 0;
        if (root.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var o) && o.TryGetInt32(out var on))
                online = on;
            if (players.TryGetProperty("max", out var m) && m.TryGetInt32(out var mx))
                max = mx;
        }

        var version = root.TryGetProperty("version", out var v) && v.TryGetProperty("name", out var name) ? name.GetString() : null;
        var motd = root.TryGetProperty("description", out var d)
            ? d.ValueKind == JsonValueKind.String ? d.GetString() : d.TryGetProperty("text", out var text) ? text.GetString() : null
            : null;
        return new ServerStatus(true, online, max, version, motd, null);
    }

    private static async Task WritePacketAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        var packet = new MemoryStream();
        WriteVarInt(packet, data.Length);
        packet.Write(data);
        await stream.WriteAsync(packet.ToArray(), ct).ConfigureAwait(false);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    internal static void WriteVarInt(Stream stream, int value)
    {
        var unsigned = (uint)value;
        do
        {
            var b = (byte)(unsigned & 0x7F);
            unsigned >>= 7;
            if (unsigned != 0)
                b |= 0x80;
            stream.WriteByte(b);
        }
        while (unsigned != 0);
    }

    private static int ReadVarInt(Stream stream)
    {
        int value = 0, shift = 0;
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                throw new EndOfStreamException();
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift > 28)
                throw new InvalidDataException("VarInt demasiado largo");
        }
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        int value = 0, shift = 0;
        var buffer = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            value |= (buffer[0] & 0x7F) << shift;
            if ((buffer[0] & 0x80) == 0)
                return value;
            shift += 7;
            if (shift > 28)
                throw new InvalidDataException("VarInt demasiado largo");
        }
    }
}
