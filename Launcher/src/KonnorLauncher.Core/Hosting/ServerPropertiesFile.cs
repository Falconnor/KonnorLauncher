using System.Globalization;
using System.Text;

namespace KonnorLauncher.Core.Hosting;

/// <summary>
/// <c>server.properties</c> con el formato de <c>java.util.Properties</c>: <c>clave=valor</c>,
/// comentarios con <c>#</c> o <c>!</c> y escapes (<c>\uXXXX</c>, <c>\\</c>, <c>\:</c>...).
/// Al guardar se conservan el orden, los comentarios y las claves que no se tocaron; los caracteres
/// no ASCII (acentos del MOTD) se escriben como <c>\uXXXX</c>, que leen igual todas las versiones.
/// </summary>
public sealed class ServerPropertiesFile
{
    public const string FileName = "server.properties";
    public const int DefaultPort = 25565;

    private readonly List<Line> _lines = [];

    private sealed class Line
    {
        public string? Raw { get; set; }   // comentario, línea vacía o línea sin tocar
        public string? Key { get; init; }
        public string? Value { get; set; }
    }

    public ServerPropertiesFile() { }

    private ServerPropertiesFile(string text)
    {
        foreach (var logical in LogicalLines(text))
        {
            var trimmed = logical.TrimStart(' ', '\t', '\f');
            if (trimmed.Length == 0 || trimmed[0] is '#' or '!')
            {
                _lines.Add(new Line { Raw = logical });
                continue;
            }

            var (key, value) = SplitPair(trimmed);
            var existing = _lines.FirstOrDefault(l => l.Key == key);
            if (existing is not null)
            {
                existing.Value = value; // en Java gana la última
                existing.Raw = null;
            }
            else
                _lines.Add(new Line { Key = key, Value = value, Raw = logical });
        }
    }

    /// <summary>Lee el archivo; si no existe o no se puede leer, devuelve uno vacío.</summary>
    public static ServerPropertiesFile Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : new ServerPropertiesFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ServerPropertiesFile();
        }
    }

    /// <summary>UTF-8 si es válido (versiones recientes); si no, ISO-8859-1 (el formato clásico de Java).</summary>
    public static ServerPropertiesFile Parse(byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        return Parse(text.TrimStart('﻿'));
    }

    public static ServerPropertiesFile Parse(string text) => new(text);

    public IEnumerable<string> Keys => _lines.Where(l => l.Key is not null).Select(l => l.Key!);

    public string? Get(string key) => _lines.FirstOrDefault(l => l.Key == key)?.Value;

    public string Get(string key, string fallback) => Get(key) ?? fallback;

    public bool GetBool(string key, bool fallback) =>
        Get(key)?.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => fallback,
        };

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>Puerto de Minecraft (<c>server-port</c>), 25565 si falta o no es válido.</summary>
    public int Port => GetInt("server-port", DefaultPort) is var port and >= 1 and <= 65535 ? port : DefaultPort;

    public void Set(string key, string value)
    {
        var line = _lines.FirstOrDefault(l => l.Key == key);
        if (line is null)
        {
            _lines.Add(new Line { Key = key, Value = value });
            return;
        }

        if (line.Value != value)
        {
            line.Value = value;
            line.Raw = null; // se reescribe
        }
    }

    public void Set(string key, bool value) => Set(key, value ? "true" : "false");
    public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var line in _lines)
        {
            if (line.Key is null)
                sb.Append(line.Raw).Append('\n');
            else if (line.Raw is not null)
                sb.Append(line.Raw).Append('\n');
            else
                sb.Append(Escape(line.Key, isKey: true)).Append('=').Append(Escape(line.Value ?? string.Empty, isKey: false)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Guarda en un temporal y lo reemplaza (el servidor nunca lee un archivo a medias).</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToString(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    // ── Formato de java.util.Properties ──────────────────────────────────

    /// <summary>Une las líneas que terminan en un número impar de <c>\</c> con la siguiente.</summary>
    private static IEnumerable<string> LogicalLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.EndsWith('\n'))
            normalized = normalized[..^1]; // el salto final no es una línea vacía más
        var physical = normalized.Length == 0 ? [] : normalized.Split('\n');
        var current = new StringBuilder();
        var continuing = false;
        foreach (var raw in physical)
        {
            var line = continuing ? raw.TrimStart(' ', '\t', '\f') : raw;
            var backslashes = 0;
            for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; i--)
                backslashes++;
            var isComment = !continuing && line.TrimStart(' ', '\t', '\f') is { Length: > 0 } t && t[0] is '#' or '!';
            if (!isComment && backslashes % 2 == 1)
            {
                current.Append(line, 0, line.Length - 1);
                continuing = true;
                continue;
            }

            current.Append(line);
            yield return current.ToString();
            current.Clear();
            continuing = false;
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static (string Key, string Value) SplitPair(string line)
    {
        var i = 0;
        var key = new StringBuilder();
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                key.Append(line, i, 2);
                i += 2;
                continue;
            }

            if (c is '=' or ':' or ' ' or '\t' or '\f')
                break;
            key.Append(c);
            i++;
        }

        // Separador: espacios, como mucho un = o :, y espacios.
        while (i < line.Length && line[i] is ' ' or '\t' or '\f')
            i++;
        if (i < line.Length && line[i] is '=' or ':')
            i++;
        while (i < line.Length && line[i] is ' ' or '\t' or '\f')
            i++;

        return (Unescape(key.ToString()), Unescape(line[i..]));
    }

    public static string Unescape(string text)
    {
        if (!text.Contains('\\'))
            return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                sb.Append(c);
                continue;
            }

            var next = text[++i];
            switch (next)
            {
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 'f': sb.Append('\f'); break;
                case 'u' when i + 4 < text.Length
                              && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: sb.Append(next); break;
            }
        }

        return sb.ToString();
    }

    public static string Escape(string text, bool isKey)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\f': sb.Append("\\f"); break;
                case ' ' when isKey || i == 0: sb.Append("\\ "); break;
                case '=' or ':' when isKey: sb.Append('\\').Append(c); break;
                case '#' or '!' when isKey && i == 0: sb.Append('\\').Append(c); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
