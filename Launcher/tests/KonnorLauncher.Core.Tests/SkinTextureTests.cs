using System.Buffers.Binary;
using KonnorLauncher.Core.Players;
using KonnorLauncher.Core.Tests.Infrastructure;
using Shouldly;

namespace KonnorLauncher.Core.Tests;

/// <summary>Validación de skins y textura para el visor 3D.</summary>
public sealed class SkinTextureTests
{
    /// <summary>Cabecera PNG mínima (firma + IHDR) con el tamaño indicado.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var header = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), 13);
        "IHDR"u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), height);
        return header;
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(64, 32)]
    public void Skin_sizes_are_accepted(int width, int height) =>
        SkinTexture.ValidateHeader(PngHeader(width, height)).ShouldBeNull();

    [Theory]
    [InlineData(128, 128)]
    [InlineData(32, 32)]
    [InlineData(64, 48)]
    public void Other_sizes_are_rejected_with_the_actual_size(int width, int height) =>
        SkinTexture.ValidateHeader(PngHeader(width, height)).ShouldContain($"{width}×{height}");

    [Fact]
    public void Files_that_are_not_png_are_rejected()
    {
        using var dir = new TempDir();
        var jpg = dir.Combine("foto.png");
        File.WriteAllBytes(jpg, [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[40]]);
        SkinTexture.Validate(jpg).ShouldBe("El archivo no es una imagen PNG.");

        var tiny = dir.Combine("corto.png");
        File.WriteAllBytes(tiny, [0x89, 0x50]);
        SkinTexture.Validate(tiny).ShouldBe("El archivo no es una imagen PNG.");

        var big = dir.Combine("grande.png");
        File.WriteAllBytes(big, [.. PngHeader(64, 64), .. new byte[SkinTexture.MaxFileBytes]]);
        SkinTexture.Validate(big)!.ShouldContain("demasiado grande");

        SkinTexture.Validate(dir.Combine("no-existe.png")).ShouldBe("El archivo no existe.");
    }

    [Fact]
    public void Classic_skins_get_mirrored_left_limbs_and_opaque_base()
    {
        var skin = new byte[64 * 32 * 4];
        Set(skin, 44, 20, 0, 0, 255, 255);   // frente del brazo derecho, primera columna: rojo
        Set(skin, 4, 20, 255, 0, 0, 128);    // frente de la pierna derecha: azul semitransparente

        var result = SkinTexture.Normalize(new SkinComposer.Pixels(64, 32, skin));

        result.Height.ShouldBe(64);
        // Brazo izquierdo: frente en (36, 52), copiado en espejo (la primera columna pasa a la última).
        Get(result, 39, 52).ShouldBe(new byte[] { 0, 0, 255, 255 });
        // La base se vuelve opaca, como en el juego.
        Get(result, 4, 20)[3].ShouldBe((byte)255);
        Get(result, 0, 0)[3].ShouldBe((byte)255);
        // La capa exterior conserva su transparencia.
        Get(result, 40, 0)[3].ShouldBe((byte)0);
    }

    [Fact]
    public void Slim_skins_are_detected_by_the_unused_arm_columns()
    {
        var classic = new byte[64 * 64 * 4];
        for (var y = 20; y < 32; y++)
            for (var x = 40; x < 56; x++)
                Set(classic, x, y, 1, 1, 1, 255);
        SkinTexture.LooksSlim(new SkinComposer.Pixels(64, 64, classic)).ShouldBeFalse();

        var slim = (byte[])classic.Clone();
        for (var y = 20; y < 32; y++)
            for (var x = 54; x < 56; x++)
                slim[(y * 64 + x) * 4 + 3] = 0;
        SkinTexture.LooksSlim(new SkinComposer.Pixels(64, 64, slim)).ShouldBeTrue();
        SkinTexture.LooksSlim(new SkinComposer.Pixels(64, 32, new byte[64 * 32 * 4])).ShouldBeFalse();
    }

    private static void Set(byte[] data, int x, int y, byte b, byte g, byte r, byte a)
    {
        var i = (y * 64 + x) * 4;
        data[i] = b;
        data[i + 1] = g;
        data[i + 2] = r;
        data[i + 3] = a;
    }

    private static byte[] Get(SkinComposer.Pixels image, int x, int y) => image.Bgra.AsSpan((y * 64 + x) * 4, 4).ToArray();
}
