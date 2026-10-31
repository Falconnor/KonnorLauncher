using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace KonnorLauncher.Core.Tests.Infrastructure;

/// <summary>
/// Servidor HTTP/1.1 mínimo sobre <see cref="TcpListener"/> en 127.0.0.1. Se usa en vez de
/// HttpListener porque no necesita permisos de administrador. Sirve las rutas registradas,
/// admite <c>Range: bytes=N-</c> (como el backend real) y puede simular cortes de conexión.
/// </summary>
public sealed partial class LocalHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _cutAfter = new(StringComparer.Ordinal);
    private readonly Task _loop;

    public LocalHttpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Rutas pedidas (tal como llegan, sin decodificar).</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Cabecera Range de cada petición (null si no la tenía), en el mismo orden.</summary>
    public ConcurrentQueue<(string Path, string? Range)> RangeRequests { get; } = new();

    /// <summary>Si es false, ignora Range y responde siempre 200 con el archivo entero.</summary>
    public bool SupportsRange { get; set; } = true;

    /// <summary>Registra el contenido de una ruta exacta (ya codificada, p. ej. <c>/client/a%20b.jar</c>).</summary>
    public void Map(string rawPath, byte[] body) => _routes[rawPath] = body;

    public void Map(string rawPath, string body) => Map(rawPath, Encoding.UTF8.GetBytes(body));

    /// <summary>La próxima petición a esa ruta enviará solo <paramref name="bytes"/> bytes y cortará la conexión.</summary>
    public void CutNextResponse(string rawPath, int bytes) => _cutAfter[rawPath] = bytes;

    [GeneratedRegex(@"^bytes=(\d+)-$")]
    private static partial Regex RangeRegex();

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync();
                if (requestLine is null)
                    return;

                string? range = null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        range = line[6..].Trim();
                }

                var path = requestLine.Split(' ')[1];
                Requests.Enqueue(path);
                RangeRequests.Enqueue((path, range));

                if (!_routes.TryGetValue(path, out var body))
                {
                    await WriteAsync(stream, "404 Not Found", Encoding.UTF8.GetBytes("not found"), null);
                    return;
                }

                var match = range is null ? null : RangeRegex().Match(range);
                if (SupportsRange && match is { Success: true })
                {
                    var from = long.Parse(match.Groups[1].Value);
                    if (from >= body.Length)
                    {
                        await WriteAsync(stream, "416 Range Not Satisfiable", [], $"Content-Range: bytes */{body.Length}\r\n");
                        return;
                    }

                    var slice = body[(int)from..];
                    await WriteAsync(stream, "206 Partial Content", slice,
                        $"Content-Range: bytes {from}-{body.Length - 1}/{body.Length}\r\n", path);
                    return;
                }

                await WriteAsync(stream, "200 OK", body, "Accept-Ranges: bytes\r\n", path);
            }
            catch (IOException)
            {
                // El cliente cerró la conexión.
            }
        }
    }

    private async Task WriteAsync(NetworkStream stream, string status, byte[] body, string? extraHeaders, string? path = null)
    {
        var header = $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\n{extraHeaders}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));

        // Corte simulado: se envía solo una parte del cuerpo y se cierra la conexión.
        if (path is not null && _cutAfter.TryRemove(path, out var cut) && cut < body.Length)
        {
            await stream.WriteAsync(body.AsMemory(0, cut));
            await stream.FlushAsync();
            return;
        }

        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch
        {
            // Cierre.
        }

        _cts.Dispose();
    }
}
