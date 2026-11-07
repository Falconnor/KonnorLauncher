using System.Text;

namespace KonnorLauncher.Core.Configuration;

/// <summary>
/// Lee y escribe <c>launcher.properties</c> (formato <c>clave=valor</c>) igual que la versión en
/// Python, para que las configuraciones existentes sigan sirviendo:
/// - La lectura es tolerante: ignora líneas vacías, comentarios (<c>#</c>) y líneas sin <c>=</c>,
///   y recorta los espacios de clave y valor.
/// - Al guardar solo reescribe la línea de la clave que cambia; los comentarios, el orden y los
///   saltos de línea se conservan. Si la clave no existe, se añade al final.
/// - El guardado es atómico (archivo temporal + <see cref="File.Move(string,string,bool)"/>), así
///   el archivo nunca queda a medias.
/// Un único candado permite usarlo desde varios hilos.
/// </summary>
public sealed class LauncherProperties
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _sync = new();
    private readonly List<string> _lines = [];
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private string _newLine = Environment.NewLine;

    public LauncherProperties(string filePath)
    {
        FilePath = filePath;
        Reload();
    }

    /// <summary>Ruta absoluta del archivo de propiedades.</summary>
    public string FilePath { get; }

    // ── Lectura ──────────────────────────────────────────────────────────

    /// <summary>Vuelve a leer el archivo desde disco (si no existe, queda vacío).</summary>
    public void Reload()
    {
        lock (_sync)
        {
            _lines.Clear();
            _values.Clear();
            if (!File.Exists(FilePath))
                return;

            string text;
            try
            {
                // ReadAllText descarta el BOM (en la versión Python quedaba pegado a la primera clave).
                text = File.ReadAllText(FilePath, Encoding.UTF8);
            }
            catch (IOException)
            {
                return; // Si el archivo no se puede leer, se trabaja con propiedades vacías.
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            _newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : (text.Contains('\n') ? "\n" : Environment.NewLine);
            var lines = text.Split('\n');
            // Si el archivo termina en salto de línea, el último elemento vacío no es una línea real.
            var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
            for (var i = 0; i < count; i++)
            {
                var raw = lines[i].TrimEnd('\r');
                _lines.Add(raw);
                if (TryParseLine(raw, out var key, out var value))
                    _values[key] = value; // Si una clave se repite, gana la última.
            }
        }
    }

    /// <summary>Devuelve el valor (recortado) o <paramref name="defaultValue"/> si la clave no existe.</summary>
    public string Get(string key, string defaultValue = "")
    {
        lock (_sync)
            return _values.TryGetValue(key, out var value) ? value : defaultValue;
    }

    /// <summary>Indica si la clave aparece en el archivo.</summary>
    public bool Contains(string key)
    {
        lock (_sync)
            return _values.ContainsKey(key);
    }

    // ── Escritura ────────────────────────────────────────────────────────

    /// <summary>Asigna una clave y guarda en el momento.</summary>
    public void Set(string key, string value) => SetMany([new KeyValuePair<string, string>(key, value)]);

    /// <summary>Asigna varias claves y escribe en disco una sola vez.</summary>
    public void SetMany(IEnumerable<KeyValuePair<string, string>> entries)
    {
        lock (_sync)
        {
            foreach (var (key, rawValue) in entries)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(key);
                // Un salto de línea en el valor rompería el formato, por eso se elimina.
                var value = rawValue.Replace("\r", string.Empty).Replace("\n", string.Empty);
                var updated = false;
                for (var i = 0; i < _lines.Count; i++)
                {
                    var raw = _lines[i];
                    var trimmed = raw.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#') || !raw.Contains('='))
                        continue;
                    if (raw.Split('=', 2)[0].Trim() == key)
                    {
                        // Se reescriben todas las líneas con esa clave (si estuviera repetida).
                        _lines[i] = $"{key}={value}";
                        updated = true;
                    }
                }

                if (!updated)
                    _lines.Add($"{key}={value}");
                _values[key] = value.Trim();
            }

            SaveLocked();
        }
    }

    /// <summary>Escribe el archivo de forma atómica (temporal en la misma carpeta + reemplazo).</summary>
    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var builder = new StringBuilder();
        foreach (var line in _lines)
            builder.Append(line).Append(_newLine);

        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, builder.ToString(), Utf8NoBom);
        File.Move(tempPath, FilePath, overwrite: true);
    }

    /// <summary>Interpreta una línea; devuelve false si está vacía, es un comentario o no tiene <c>=</c>.</summary>
    internal static bool TryParseLine(string raw, out string key, out string value)
    {
        key = value = string.Empty;
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
            return false;
        var index = line.IndexOf('=');
        if (index < 0)
            return false;
        key = line[..index].Trim();
        value = line[(index + 1)..].Trim();
        return true;
    }
}
