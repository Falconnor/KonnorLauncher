using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KonnorLauncher.Core.Tests.Infrastructure;

/// <summary>
/// Servidor HTTP/1.1 mínimo sobre <see cref="TcpListener"/> en 127.0.0.1. Se usa en vez de
/// HttpListener porque no necesita permisos de administrador. Sirve las rutas registradas y anota cada petición.
/// </summary>
public sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, byte[]> _routes = new(StringComparer.Ordinal);
    private readonly Task _loop;

    public LocalHttpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Rutas pedidas (tal como llegan, sin decodificar).</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Registra el contenido de una ruta exacta (ya codificada, p. ej. <c>/client/a%20b.jar</c>).</summary>
    public void Map(string rawPath, byte[] body) => _routes[rawPath] = body;

    public void Map(string rawPath, string body) => Map(rawPath, Encoding.UTF8.GetBytes(body));

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
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            if (requestLine is null)
                return;
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
                // Las cabeceras no se necesitan.
            }

            var path = requestLine.Split(' ')[1];
            Requests.Enqueue(path);
            var found = _routes.TryGetValue(path, out var body);
            body ??= Encoding.UTF8.GetBytes("not found");
            var header = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
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
