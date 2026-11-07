using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KonnorLauncher.Core.Modrinth;
using KonnorLauncher.Core.Paths;
using SkiaSharp;

namespace KonnorLauncher.App.Services;

/// <summary>
/// Iconos de los proyectos de Modrinth (y las imágenes de su galería). Se guardan en memoria y en disco
/// (<see cref="LauncherPaths.CacheDir"/>\icons), así que al volver a abrir el launcher no se descargan
/// otra vez.
/// <para>
/// Modrinth los sirve casi todos en WebP. Se decodifican con SkiaSharp, no con el códec de Windows: ese
/// es una extensión de la Store que tarda unos 10 s en arrancar la primera vez de cada sesión, puede no
/// arrancar sin conexión y en algunos Windows (ediciones N, LTSC, sin Store) no existe. El resto de
/// formatos (PNG, JPG, GIF) los decodifica Windows. En disco se guardan ya convertidos a PNG y a su
/// tamaño final.
/// </para>
/// </summary>
public sealed class IconLoader(IHttpClientFactory httpFactory)
{
    /// <summary>Se decodifica al doble del tamaño en pantalla (92 px) para que se vea nítido.</summary>
    private const int DecodeWidth = 184;
    private const int MaxIconBytes = 8 * 1024 * 1024; // también imágenes de la galería
    private const int MaxCached = 600;
    private const int MaxDiskFiles = 2000;

    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new();
    private readonly ConcurrentQueue<string> _order = new();
    private int _diskPruned;

    private static string DiskDir => Path.Combine(LauncherPaths.CacheDir, "icons");

    /// <summary>Imagen ya decodificada a <paramref name="decodeWidth"/> píxeles de ancho (los iconos, al doble de 92).</summary>
    public Task<ImageSource?> LoadAsync(string? url, int decodeWidth = DecodeWidth)
    {
        if (!ContentInstaller.IsAllowedUrl(url, out _))
            return Task.FromResult<ImageSource?>(null);
        var key = $"{decodeWidth}|{url}";
        if (_cache.TryGetValue(key, out var existing))
            return existing;

        var task = LoadCoreAsync(url!, decodeWidth);
        if (!_cache.TryAdd(key, task))
            return _cache.TryGetValue(key, out var raced) ? raced : task;
        _order.Enqueue(key);
        // Lleno: fuera los más antiguos (no todos de golpe)
        while (_cache.Count > MaxCached && _order.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
        // Un fallo (corte de red, tiempo agotado) no se recuerda: la próxima vez se vuelve a intentar
        _ = task.ContinueWith(t =>
        {
            if (t.Result is null)
                _cache.TryRemove(new KeyValuePair<string, Task<ImageSource?>>(key, task));
        }, TaskScheduler.Default);
        return task;
    }

    private async Task<ImageSource?> LoadCoreAsync(string url, int decodeWidth)
    {
        PruneDiskOnce();
        var file = Path.Combine(DiskDir, FileName($"{decodeWidth}|{url}"));
        if (await TryReadAsync(file).ConfigureAwait(false) is { } saved)
        {
            if (Decode(saved, decodeWidth: 0) is { } image) // ya está a su tamaño
                return image;
            TryDelete(file); // dañado: se vuelve a descargar
        }

        var bytes = await DownloadAsync(url).ConfigureAwait(false);
        if (bytes is null)
            return null;
        var decoded = Decode(bytes, decodeWidth);
        if (decoded is BitmapSource bitmap && ToPng(bitmap) is { } png)
            await TryWriteAsync(file, png).ConfigureAwait(false);
        return decoded;
    }

    private async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await httpFactory.CreateClient(ModrinthClient.HttpClientName)
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxIconBytes)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return bytes.Length > MaxIconBytes ? null : bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodifica la imagen (fuera del hilo de la interfaz: queda congelada). WebP, con SkiaSharp; si
    /// falla, o con otros formatos, con Windows. null si no se puede.
    /// </summary>
    private static ImageSource? Decode(byte[] bytes, int decodeWidth) =>
        (IsWebP(bytes) ? DecodeWebP(bytes, decodeWidth) : null) ?? DecodeWithWindows(bytes, decodeWidth);

    /// <summary>Cabecera RIFF....WEBP.</summary>
    private static bool IsWebP(byte[] b) =>
        b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';

    /// <summary>WebP con SkiaSharp (el primer fotograma si es animado), reducido a <paramref name="decodeWidth"/> si es más ancho.</summary>
    private static BitmapSource? DecodeWebP(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null)
                return null;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var full = new SKBitmap(info);
            if (codec.GetPixels(info, full.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                return null;

            SKBitmap? scaled = null;
            if (decodeWidth > 0 && full.Width > decodeWidth)
            {
                var height = Math.Max(1, (int)Math.Round(full.Height * (double)decodeWidth / full.Width));
                scaled = full.Resize(info.WithSize(decodeWidth, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
            }

            try
            {
                var bitmap = scaled ?? full;
                // Mismo formato de píxeles en los dos lados (BGRA premultiplicado): copia directa
                var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null,
                    bitmap.GetPixels(), bitmap.RowBytes * bitmap.Height, bitmap.RowBytes);
                source.Freeze();
                return source;
            }
            finally
            {
                scaled?.Dispose();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DllNotFoundException
                                       or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null; // sin la librería nativa o un WebP raro: lo intenta Windows
        }
    }

    private static ImageSource? DecodeWithWindows(byte[] bytes, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0)
                image.DecodePixelWidth = decodeWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or IOException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>La imagen ya decodificada, como PNG (lo que se guarda en disco). null si no se puede.</summary>
    private static byte[]? ToPng(BitmapSource bitmap)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or IOException
                                       or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Nombre del archivo en disco: el hash de la dirección y el tamaño (nunca la dirección en sí).</summary>
    private static string FileName(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40] + ".png";

    private static async Task<byte[]?> TryReadAsync(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow); // para conservar las que se usan
            return bytes.Length is > 0 and <= MaxIconBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task TryWriteAsync(string file, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(DiskDir);
            var tmp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin caché en disco no pasa nada: se descargará la próxima vez.
        }
    }

    /// <summary>Una vez por sesión, en segundo plano: si hay demasiados archivos, se borran los menos usados.</summary>
    private void PruneDiskOnce()
    {
        if (Interlocked.Exchange(ref _diskPruned, 1) == 1)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(DiskDir))
                    return;
                var files = new DirectoryInfo(DiskDir).GetFiles();
                foreach (var stale in files.Where(f => f.Name.EndsWith(".tmp", StringComparison.Ordinal)))
                    TryDelete(stale.FullName);
                foreach (var old in files.Where(f => f.Name.EndsWith(".png", StringComparison.Ordinal))
                             .OrderByDescending(f => f.LastAccessTimeUtc).Skip(MaxDiskFiles))
                    TryDelete(old.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        });
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
