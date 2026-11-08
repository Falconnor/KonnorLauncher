namespace KonnorLauncher.Core.Launching;

/// <summary>
/// Ajustes de <c>options.txt</c> que el launcher fija antes de lanzar. Solo se tocan esas líneas: el resto de
/// opciones del jugador se conserva tal cual.
/// </summary>
public static class GameOptions
{
    public const string FileName = "options.txt";

    /// <summary>
    /// La primera vez que se abre una instancia, Minecraft (1.19.4+) muestra el menú de accesibilidad antes del
    /// menú principal (y no entra directamente a un servidor). Con <c>onboardAccessibility:false</c> se salta.
    /// Las versiones que no lo conocen ignoran la línea.
    /// </summary>
    public static void SkipAccessibilityOnboarding(string gameDir) => Set(Path.Combine(gameDir, FileName), "onboardAccessibility", "false");

    /// <summary>Pone <paramref name="key"/>:<paramref name="value"/> (lo añade si falta; no reescribe si ya está así).</summary>
    public static void Set(string path, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var wanted = $"{key}:{value}";
        var index = lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        if (index >= 0 && lines[index] == wanted)
            return;
        if (index >= 0)
            lines[index] = wanted;
        else
            lines.Add(wanted);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
