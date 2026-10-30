using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KonnorLauncher.Core.Players;

namespace KonnorLauncher.App.Views;

/// <summary>
/// Vista previa 2D de la skin (frente y espalda). La composición de píxeles se hace en
/// <see cref="SkinComposer"/> (Core); aquí solo se decodifica el PNG y se muestra escalado ×8
/// con vecino más cercano para que se vea pixelado.
/// </summary>
public partial class SkinPreviewWindow : Window
{
    private static readonly Uri SteveUri = new("pack://application:,,,/Assets/steve.png");

    /// <param name="skinPath">Ruta del PNG; null → steve.png incrustado.</param>
    public SkinPreviewWindow(string? skinPath)
    {
        InitializeComponent();
        FileNameText.Text = skinPath is null ? "steve.png" : Path.GetFileName(skinPath);

        try
        {
            var pixels = LoadPixels(skinPath);
            FrontImage.Source = ToBitmap(SkinComposer.ComposeFront(pixels));
            BackImage.Source = ToBitmap(SkinComposer.ComposeBack(pixels));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            ErrorText.Visibility = Visibility.Visible;
            ViewsPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Decodifica el PNG a BGRA de 32 bits.</summary>
    private static SkinComposer.Pixels LoadPixels(string? path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // no deja el archivo bloqueado
        if (path is null)
            image.UriSource = SteveUri;
        else
            image.UriSource = new Uri(Path.GetFullPath(path));
        image.EndInit();

        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var data = new byte[width * height * 4];
        bgra.CopyPixels(data, width * 4, 0);
        return new SkinComposer.Pixels(width, height, data);
    }

    /// <summary>Convierte el lienzo 16×32 a un bitmap de WPF.</summary>
    private static BitmapSource ToBitmap(SkinComposer.Pixels pixels)
    {
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra, pixels.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
