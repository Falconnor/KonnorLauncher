using System.Globalization;

namespace KonnorLauncher.Core.Configuration;

/// <summary>Memoria RAM instalada, para limitar la que se puede asignar al juego.</summary>
public static class SystemMemory
{
    /// <summary>Mínimo que se puede asignar a una instancia.</summary>
    public const int MinMb = 1024;

    /// <summary>Paso del deslizador (512 MB).</summary>
    public const int StepMb = 512;

    /// <summary>RAM física total en MB (8 GB si no se puede saber).</summary>
    public static int TotalMb { get; } = Detect();

    /// <summary>Máximo del deslizador: la RAM instalada, redondeada hacia abajo al paso.</summary>
    public static int MaxMb => Math.Max(MinMb * 2, TotalMb / StepMb * StepMb);

    /// <summary>4096 → "4 GB"; 6656 → "6,5 GB".</summary>
    public static string Format(int mb) =>
        (mb / 1024.0).ToString("0.#", CultureInfo.GetCultureInfo("es-ES")) + " GB";

    /// <summary>
    /// "6", "6 GB", "6,5", "6.5g" → GB; "6144", "6144 MB", "6144m" → MB. Un número sin unidad es GB
    /// si es pequeño (hasta 256) y MB si es mayor. null si no se entiende.
    /// </summary>
    public static int? Parse(string? text)
    {
        var value = (text ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty);
        var isMb = value.EndsWith("mb") || value.EndsWith('m');
        var isGb = value.EndsWith("gb") || value.EndsWith('g');
        value = value.TrimEnd('b', 'g', 'm').Replace(',', '.');
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return null;
        var gb = isGb || (!isMb && number <= 256);
        return (int)Math.Round(gb ? number * 1024 : number);
    }

    /// <summary>Ajusta al paso de 512 MB y al rango permitido.</summary>
    public static int Clamp(double mb) =>
        (int)Math.Clamp(Math.Round(mb / StepMb) * StepMb, MinMb, MaxMb);

    private static int Detect()
    {
        // En Windows es la memoria física total (salvo que un contenedor limite el proceso).
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return bytes > 0 ? (int)Math.Min(int.MaxValue, bytes / (1024 * 1024)) : 8192;
    }
}
