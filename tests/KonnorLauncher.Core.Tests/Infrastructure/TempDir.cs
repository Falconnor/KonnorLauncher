namespace KonnorLauncher.Core.Tests.Infrastructure;

/// <summary>Carpeta temporal única que se borra al terminar la prueba.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "konnor-tests", Guid.NewGuid().ToString("N"));

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Escribe un archivo creando las carpetas intermedias.</summary>
    public string Write(string relative, string content)
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Mejor esfuerzo.
        }
    }
}
