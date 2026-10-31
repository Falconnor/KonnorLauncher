namespace KonnorLauncher.Core.Players;

/// <summary>
/// Composición 2D de una skin de Minecraft (FRENTE y ESPALDA) sobre un lienzo de 16×32 px.
/// Trabaja con píxeles BGRA de 32 bits y sin UI, por lo que se puede probar con tests.
/// Skin moderna = exactamente 64×64; cualquier otra se trata como clásica (64×32) y las
/// extremidades del lado derecho se obtienen espejando las izquierdas.
/// </summary>
public static class SkinComposer
{
    public const int CanvasWidth = 16;
    public const int CanvasHeight = 32;

    /// <summary>Parte a copiar: origen (x, y, ancho, alto), destino (x, y) y si se espeja horizontalmente.</summary>
    private readonly record struct Part(int Sx, int Sy, int W, int H, int Dx, int Dy, bool Mirror = false);

    /// <summary>Imagen BGRA (stride = ancho × 4).</summary>
    public sealed record Pixels(int Width, int Height, byte[] Bgra);

    public static Pixels ComposeFront(Pixels skin) => Compose(skin, FrontParts(IsModern(skin)));

    public static Pixels ComposeBack(Pixels skin) => Compose(skin, BackParts(IsModern(skin)));

    public static bool IsModern(Pixels skin) => skin.Width == 64 && skin.Height == 64;

    // Partes de la skin (el orden importa: la capa del sombrero se dibuja al final).
    private static Part[] FrontParts(bool modern) =>
    [
        new(8, 8, 8, 8, 4, 0),       // cabeza
        new(20, 20, 8, 12, 4, 8),    // cuerpo
        new(44, 20, 4, 12, 0, 8),    // brazo (izquierda en pantalla)
        modern ? new(36, 52, 4, 12, 12, 8) : new(44, 20, 4, 12, 12, 8, Mirror: true), // brazo (derecha)
        new(4, 20, 4, 12, 4, 20),    // pierna (izquierda)
        modern ? new(20, 52, 4, 12, 8, 20) : new(4, 20, 4, 12, 8, 20, Mirror: true),  // pierna (derecha)
        new(40, 8, 8, 8, 4, 0),      // capa del sombrero
    ];

    private static Part[] BackParts(bool modern) =>
    [
        new(24, 8, 8, 8, 4, 0),
        new(32, 20, 8, 12, 4, 8),
        new(52, 20, 4, 12, 12, 8),
        modern ? new(44, 52, 4, 12, 0, 8) : new(52, 20, 4, 12, 0, 8, Mirror: true),
        new(12, 20, 4, 12, 8, 20),
        modern ? new(28, 52, 4, 12, 4, 20) : new(12, 20, 4, 12, 4, 20, Mirror: true),
        new(56, 8, 8, 8, 4, 0),
    ];

    private static Pixels Compose(Pixels skin, IEnumerable<Part> parts)
    {
        var output = new byte[CanvasWidth * CanvasHeight * 4];
        foreach (var part in parts)
        {
            for (var y = 0; y < part.H; y++)
            {
                for (var x = 0; x < part.W; x++)
                {
                    var srcX = part.Sx + (part.Mirror ? part.W - 1 - x : x);
                    var srcY = part.Sy + y;
                    if (srcX >= skin.Width || srcY >= skin.Height)
                        continue; // fuera de la imagen: queda transparente
                    var src = (srcY * skin.Width + srcX) * 4;
                    var dst = ((part.Dy + y) * CanvasWidth + part.Dx + x) * 4;
                    AlphaBlend(skin.Bgra, src, output, dst);
                }
            }
        }

        return new Pixels(CanvasWidth, CanvasHeight, output);
    }

    /// <summary>Mezcla "source over": así la capa del sombrero respeta su transparencia.</summary>
    private static void AlphaBlend(byte[] src, int s, byte[] dst, int d)
    {
        var sa = src[s + 3];
        if (sa == 0)
            return;
        if (sa == 255 || dst[d + 3] == 0)
        {
            Buffer.BlockCopy(src, s, dst, d, 4);
            return;
        }

        var a = sa / 255.0;
        var da = dst[d + 3] / 255.0;
        var outA = a + da * (1 - a);
        for (var i = 0; i < 3; i++)
            dst[d + i] = (byte)Math.Round((src[s + i] * a + dst[d + i] * da * (1 - a)) / outA);
        dst[d + 3] = (byte)Math.Round(outA * 255);
    }
}
