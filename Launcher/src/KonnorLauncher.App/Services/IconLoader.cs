using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KonnorLauncher.Core.Modrinth;

namespace KonnorLauncher.App.Services;

/// <summary>
/// Iconos de los proyectos de Modrinth, descargados una vez y guardados en memoria. Modrinth los sirve
/// en WebP: si Windows no tiene el códec, no se muestran y la tarjeta usa la inicial del proyecto.
/// </summary>
public sealed class IconLoader(IHttpClientFactory httpFactory)
{
    /// <summary>Se decodifica al doble del tamaño en pantalla (92 px) para que se vea nítido.</summary>
    private const int DecodeWidth = 184;
    private const int MaxIconBytes = 8 * 1024 * 1024; // también imágenes de la galería
    private const int MaxCached = 600;

    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new();

    /// <summary>Imagen ya decodificada a <paramref name="decodeWidth"/> píxeles de ancho (los iconos, al doble de 92).</summary>
    public Task<ImageSource?> LoadAsync(string? url, int decodeWidth = DecodeWidth)
    {
        if (!ContentInstaller.IsAllowedUrl(url, out _))
            return Task.FromResult<ImageSource?>(null);
        if (_cache.Count > MaxCached)
            _cache.Clear();
        return _cache.GetOrAdd($"{decodeWidth}|{url}", _ => DownloadAsync(url!, decodeWidth));
    }

    private async Task<ImageSource?> DownloadAsync(string url, int decodeWidth)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await httpFactory.CreateClient(ModrinthClient.HttpClientName)
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxIconBytes)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length > MaxIconBytes)
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze(); // se crea fuera del hilo de la interfaz
            return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
                                       or NotSupportedException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }
}
