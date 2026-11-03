using System.Buffers.Binary;

namespace KonnorLauncher.Core.Players;

/// <summary>
/// Skin de Minecraft: validación del archivo y textura lista para el visor 3D. Sin UI (los píxeles los
/// decodifica la aplicación), así que se puede probar con tests.
/// </summary>
public static class SkinTexture
{
    /// <summary>Tamaño máximo del archivo: una skin real ocupa unos pocos KB.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Comprueba el archivo antes de decodificarlo: PNG de 64×64 (moderna) o 64×32 (clásica, anterior a
    /// la 1.8) y como mucho <see cref="MaxFileBytes"/>. Devuelve el motivo si no vale, o null.
    /// </summary>
    public static string? Validate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return "El archivo no existe.";
            if (info.Length > MaxFileBytes)
                return "El archivo es demasiado grande para ser una skin (máximo 1 MB).";
            var header = new byte[24];
            using (var stream = info.OpenRead())
            {
                if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                    return "El archivo no es una imagen PNG.";
            }

            return ValidateHeader(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"No se pudo leer el archivo: {ex.Message}";
        }
    }

    /// <summary>Firma PNG y tamaño de la cabecera IHDR (los primeros 24 bytes).</summary>
    public static string? ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 24 || !header[..8].SequenceEqual(PngSignature) || !header[12..16].SequenceEqual("IHDR"u8))
            return "El archivo no es una imagen PNG.";
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        return width == 64 && height is 64 or 32
            ? null
            : $"Una skin mide 64×64 o 64×32 píxeles; esta imagen mide {width}×{height}.";
    }

    /// <summary>
    /// Textura de 64×64 como la usa el juego: una clásica (64×32) se amplía copiando en espejo la pierna
    /// y el brazo derechos a los izquierdos, y las capas base se vuelven opacas (el juego ignora su
    /// transparencia; solo las capas exteriores la tienen).
    /// </summary>
    public static SkinComposer.Pixels Normalize(SkinComposer.Pixels skin)
    {
        if (skin.Width != 64 || skin.Height is not (64 or 32))
            throw new ArgumentException("La skin debe medir 64×64 o 64×32", nameof(skin));

        var output = new byte[64 * 64 * 4];
        Buffer.BlockCopy(skin.Bgra, 0, output, 0, Math.Min(skin.Bgra.Length, output.Length));
        var result = new SkinComposer.Pixels(64, 64, output);

        if (skin.Height == 32)
        {
            // Mismas copias que hace el juego con las skins antiguas (región, desplazamiento, tamaño).
            CopyMirrored(result, 4, 16, 16, 32, 4, 4);
            CopyMirrored(result, 8, 16, 16, 32, 4, 4);
            CopyMirrored(result, 0, 20, 24, 32, 4, 12);
            CopyMirrored(result, 4, 20, 16, 32, 4, 12);
            CopyMirrored(result, 8, 20, 8, 32, 4, 12);
            CopyMirrored(result, 12, 20, 16, 32, 4, 12);
            CopyMirrored(result, 44, 16, -8, 32, 4, 4);
            CopyMirrored(result, 48, 16, -8, 32, 4, 4);
            CopyMirrored(result, 40, 20, 0, 32, 4, 12);
            CopyMirrored(result, 44, 20, -8, 32, 4, 12);
            CopyMirrored(result, 48, 20, -16, 32, 4, 12);
            CopyMirrored(result, 52, 20, -8, 32, 4, 12);
        }

        // Capas base: cabeza, cuerpo y extremidades derechas, y extremidades izquierdas.
        SetOpaque(result, 0, 0, 32, 16);
        SetOpaque(result, 0, 16, 64, 32);
        SetOpaque(result, 16, 48, 48, 64);
        return result;
    }

    /// <summary>
    /// Modelo delgado (brazos de 3 píxeles): en las skins delgadas la última columna del frente del
    /// brazo derecho, y la de la espalda, son transparentes. Las clásicas nunca lo son.
    /// </summary>
    public static bool LooksSlim(SkinComposer.Pixels skin)
    {
        if (skin.Width != 64 || skin.Height != 64)
            return false;
        for (var y = 20; y < 32; y++)
        {
            for (var x = 54; x < 56; x++)
            {
                if (skin.Bgra[(y * 64 + x) * 4 + 3] != 0)
                    return false;
            }
        }

        return true;
    }

    private static void CopyMirrored(SkinComposer.Pixels image, int x, int y, int dx, int dy, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var src = ((y + row) * 64 + x + col) * 4;
                var dst = ((y + dy + row) * 64 + x + dx + (width - 1 - col)) * 4;
                Buffer.BlockCopy(image.Bgra, src, image.Bgra, dst, 4);
            }
        }
    }

    private static void SetOpaque(SkinComposer.Pixels image, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
                image.Bgra[(y * 64 + x) * 4 + 3] = 255;
        }
    }
}
