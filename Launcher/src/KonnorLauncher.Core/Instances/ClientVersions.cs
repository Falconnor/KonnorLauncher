namespace KonnorLauncher.Core.Instances;

/// <summary>Comparación de las <c>client_version</c> que anuncia el servidor (<c>1.0.10</c>, <c>2.1-beta</c>...).</summary>
public static class ClientVersions
{
    private static readonly char[] Separators = ['.', '-', '_', '+', ' '];

    /// <summary>
    /// Compara por partes: las numéricas como números (<c>1.0.10</c> &gt; <c>1.0.9</c>) y el resto como
    /// texto sin distinguir mayúsculas. Una parte que falta cuenta como 0 (<c>1.0</c> = <c>1.0.0</c>).
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var left = (a ?? string.Empty).Trim().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var right = (b ?? string.Empty).Trim().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : "0";
            var y = i < right.Length ? right[i] : "0";
            var result = long.TryParse(x, out var nx) && long.TryParse(y, out var ny)
                ? nx.CompareTo(ny)
                : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (result != 0)
                return Math.Sign(result);
        }

        return 0;
    }

    /// <summary>
    /// El servidor anuncia una versión posterior a la instalada. Sin versión instalada, cualquier
    /// versión anunciada es una actualización.
    /// </summary>
    public static bool IsNewer(string? available, string? installed) =>
        !string.IsNullOrWhiteSpace(available)
        && (string.IsNullOrWhiteSpace(installed) || Compare(available, installed) > 0);
}
