using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Paths;

namespace KonnorLauncher.App.Services;

/// <summary>
/// Icono de cada hosting de terceros: el de su web (<c>favicon.ico</c>), como lo muestra un navegador. Se descarga una
/// vez de su propio dominio y se guarda en la caché (<c>favicons</c>). Para «Panel web», el del dominio de ese panel.
/// </summary>
public static class SiteIcons
{
    public const string HttpClientName = "favicon";
    private const int MaxBytes = 512 * 1024;

    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private static string CacheDir => Path.Combine(LauncherPaths.CacheDir, "favicons");

    /// <summary>Sitio del que se toma el icono (el panel de Hostinger no lo sirve: el de su web).</summary>
    public static Uri? SiteOf(ThirdPartyEntry entry) => entry.Provider switch
    {
        ThirdPartyProvider.Exaroton => new Uri("https://exaroton.com/"),
        ThirdPartyProvider.Aternos => new Uri("https://aternos.org/"),
        ThirdPartyProvider.Hostinger => new Uri("https://www.hostinger.com/"),
        ThirdPartyProvider.Modrinth => new Uri("https://modrinth.com/"),
        _ => ThirdPartyEntry.IsValidPanel(entry.PanelUrl, out var panel) ? new Uri(panel.GetLeftPart(UriPartial.Authority) + "/") : null,
    };

    /// <summary>Icono del hosting; null si su web no tiene o no se pudo descargar (se ve la inicial).</summary>
    public static Task<ImageSource?> GetAsync(IHttpClientFactory httpFactory, ThirdPartyEntry entry) =>
        SiteOf(entry) is { } site ? Loaded.GetOrAdd(site.Host, _ => LoadAsync(httpFactory, site)) : Task.FromResult<ImageSource?>(null);

    private static async Task<ImageSource?> LoadAsync(IHttpClientFactory httpFactory, Uri site)
    {
        var file = Path.Combine(CacheDir, site.Host + ".ico");
        try
        {
            byte[]? data = File.Exists(file) ? await File.ReadAllBytesAsync(file).ConfigureAwait(false) : null;
            if (data is null)
            {
                using var response = await httpFactory.CreateClient(HttpClientName).GetAsync(new Uri(site, "/favicon.ico")).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
                    return null;
                data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (data.Length is 0 or > MaxBytes || !IsImage(data))
                    return null; // algunas webs devuelven su página en lugar del icono
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllBytesAsync(file, data).ConfigureAwait(false);
            }

            return Decode(data);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>ICO (<c>00 00 01 00</c>), PNG, GIF o JPEG por sus primeros bytes.</summary>
    private static bool IsImage(byte[] d) =>
        d.Length > 4 && ((d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0)
                         || (d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G')
                         || (d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F')
                         || (d[0] == 0xFF && d[1] == 0xD8));

    /// <summary>El fotograma más grande del icono (un .ico trae varios tamaños), congelado.</summary>
    private static ImageSource Decode(byte[] data)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        frame.Freeze();
        return frame;
    }
}
