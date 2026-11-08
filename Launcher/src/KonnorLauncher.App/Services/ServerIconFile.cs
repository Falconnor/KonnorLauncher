using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KonnorLauncher.App.Services;

/// <summary>
/// <c>server-icon.png</c>: el icono que muestra la lista de servidores del juego. Minecraft solo lo acepta en PNG
/// de 64×64, así que cualquier imagen se recorta al centro (cuadrada) y se escala.
/// </summary>
public static class ServerIconFile
{
    public const string FileName = "server-icon.png";
    public const int Size = 64;

    /// <summary>El icono actual (sin bloquear el archivo); null si no hay o no se puede leer.</summary>
    public static ImageSource? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>Convierte <paramref name="source"/> (PNG, JPG, BMP, GIF…) en el icono del servidor.</summary>
    public static void SaveFrom(string source, string destination)
    {
        var decoder = BitmapDecoder.Create(new Uri(source), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];

        // Cuadrado del centro.
        var side = Math.Min(frame.PixelWidth, frame.PixelHeight);
        BitmapSource square = new CroppedBitmap(frame, new Int32Rect((frame.PixelWidth - side) / 2, (frame.PixelHeight - side) / 2, side, side));

        // Escalado con buena calidad (o sin suavizar si ya es pixel art pequeño).
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, side <= Size ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(square, new Rect(0, 0, Size, Size));
        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        var tmp = destination + ".tmp";
        using (var stream = File.Create(tmp))
            encoder.Save(stream);
        File.Move(tmp, destination, overwrite: true);
    }
}
