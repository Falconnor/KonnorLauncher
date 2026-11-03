using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KonnorLauncher.Core.Players;

namespace KonnorLauncher.App.Services;

/// <summary>Imágenes de la cuenta: decodificar la skin y preparar la foto de perfil.</summary>
public static class PlayerImages
{
    private static readonly Uri SteveUri = new("pack://application:,,,/Assets/steve.png");

    /// <summary>Lado de la foto de perfil guardada (se recorta cuadrada y se reduce).</summary>
    public const int AvatarSize = 256;

    /// <summary>Tamaño máximo de la imagen elegida como foto de perfil.</summary>
    public const long MaxAvatarBytes = 20 * 1024 * 1024;

    /// <summary>Skin de Steve incluida en el launcher, ya normalizada.</summary>
    public static SkinComposer.Pixels Steve() => SkinTexture.Normalize(Decode(SteveUri));

    /// <summary>
    /// Valida y decodifica una skin. Devuelve la textura normalizada, o el motivo por el que no vale.
    /// </summary>
    public static (SkinComposer.Pixels? Skin, bool LooksSlim, string? Error) LoadSkin(string path)
    {
        if (SkinTexture.Validate(path) is { } error)
            return (null, false, error);
        try
        {
            var pixels = Decode(new Uri(Path.GetFullPath(path)));
            if (pixels.Width != 64 || pixels.Height is not (64 or 32))
                return (null, false, "La imagen no tiene el tamaño de una skin.");
            return (SkinTexture.Normalize(pixels), SkinTexture.LooksSlim(pixels), null);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException
                                       or ArgumentException or UnauthorizedAccessException or FileFormatException)
        {
            return (null, false, "La imagen está dañada o no se puede leer.");
        }
    }

    /// <summary>
    /// Foto de perfil: se recorta el centro cuadrado y se reduce a <see cref="AvatarSize"/>. Devuelve la
    /// imagen lista para guardar, o el motivo por el que no se puede usar.
    /// </summary>
    public static (BitmapSource? Image, string? Error) LoadAvatar(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxAvatarBytes)
                return (null, "La imagen es demasiado grande (máximo 20 MB).");
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // no deja el archivo bloqueado
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.EndInit();

            var side = Math.Min(image.PixelWidth, image.PixelHeight);
            if (side < 16)
                return (null, "La imagen es demasiado pequeña.");
            BitmapSource square = new CroppedBitmap(image, new System.Windows.Int32Rect(
                (image.PixelWidth - side) / 2, (image.PixelHeight - side) / 2, side, side));
            if (side > AvatarSize)
                square = new TransformedBitmap(square, new ScaleTransform((double)AvatarSize / side, (double)AvatarSize / side));
            var result = new FormatConvertedBitmap(square, PixelFormats.Bgra32, null, 0);
            result.Freeze();
            return (result, null);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException
                                       or ArgumentException or UnauthorizedAccessException or FileFormatException)
        {
            return (null, "No es una imagen válida (PNG, JPG, BMP o GIF).");
        }
    }

    /// <summary>Guarda la foto como PNG (en un temporal y reemplazando: nunca queda a medias).</summary>
    public static void SaveAvatar(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(tmp))
            encoder.Save(stream);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Foto guardada, o null si no hay o no se puede leer.</summary>
    public static ImageSource? OpenAvatar(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // se relee si cambió
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException
                                       or UnauthorizedAccessException or FileFormatException)
        {
            return null;
        }
    }

    private static SkinComposer.Pixels Decode(Uri uri)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = uri;
        image.EndInit();

        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var data = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(data, bgra.PixelWidth * 4, 0);
        return new SkinComposer.Pixels(bgra.PixelWidth, bgra.PixelHeight, data);
    }
}
