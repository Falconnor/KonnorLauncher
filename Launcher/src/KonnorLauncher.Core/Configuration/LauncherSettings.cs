using System.Globalization;

namespace KonnorLauncher.Core.Configuration;

/// <summary>
/// Vista tipada sobre <see cref="LauncherProperties"/>. Mantiene las mismas claves que el launcher
/// en Python, así ninguna configuración existente necesita migrarse.
/// </summary>
public sealed class LauncherSettings
{
    // Claves de launcher.properties. No deben cambiarse: las instalaciones existentes dependen de ellas.
    public const string KeyGamePath = "game.path";
    public const string KeyJavaPath = "java.path";
    public const string KeyVersionsPath = "versions.path";
    public const string KeyMinecraftVersion = "minecraft.version";
    public const string KeyServerUrl = "server.url";
    public const string KeyEnableVerify = "enable.verify";
    public const string KeyDeleteUnauthorized = "delete_unauthorized";
    public const string KeyCachedClientVersion = "cached.client.version";
    public const string KeyPlayerUsername = "player.username";
    public const string KeyJavaMemory = "java.memory";
    public const string KeyPlayerSkin = "player.skin";

    /// <summary>Modelo de la skin: <c>classic</c> (brazos de 4 píxeles) o <c>slim</c> (3 píxeles).</summary>
    public const string KeyPlayerSkinModel = "player.skin.model";

    /// <summary>Foto de perfil del launcher (PNG en la carpeta de datos).</summary>
    public const string KeyPlayerAvatar = "player.avatar";
    public const string KeyPlayerUuid = "player.uuid";
    public const string KeyPlayerAccessToken = "player.accessToken";

    /// <summary>Instancia seleccionada en la barra lateral (id = nombre de su carpeta).</summary>
    public const string KeySelectedInstance = "instance.selected";

    /// <summary>Memoria por defecto si el archivo no dice nada.</summary>
    public const string DefaultMemory = "4G";

    public LauncherSettings(LauncherProperties properties) => Properties = properties;

    /// <summary>Archivo subyacente (para guardar varias claves a la vez o recargar).</summary>
    public LauncherProperties Properties { get; }

    public string GamePath => Properties.Get(KeyGamePath);
    public string JavaPath => Properties.Get(KeyJavaPath);
    public string VersionsPath => Properties.Get(KeyVersionsPath, "versions");
    public string MinecraftVersion => Properties.Get(KeyMinecraftVersion);
    public string ServerUrl => Properties.Get(KeyServerUrl);
    public string CachedClientVersion => Properties.Get(KeyCachedClientVersion);
    public string PlayerUsername => Properties.Get(KeyPlayerUsername);
    public string PlayerSkin => Properties.Get(KeyPlayerSkin);
    public bool PlayerSkinSlim => string.Equals(Properties.Get(KeyPlayerSkinModel), "slim", StringComparison.OrdinalIgnoreCase);
    public string PlayerAvatar => Properties.Get(KeyPlayerAvatar);
    public string JavaMemory => Properties.Get(KeyJavaMemory, DefaultMemory);

    /// <summary>UUID manual (no está en el archivo por defecto); null si no existe o está vacío.</summary>
    public string? PlayerUuid => NullIfEmpty(Properties.Get(KeyPlayerUuid));

    /// <summary>Token manual (no está en el archivo por defecto); null si no existe o está vacío.</summary>
    public string? PlayerAccessToken => NullIfEmpty(Properties.Get(KeyPlayerAccessToken));

    /// <summary>Solo el literal <c>true</c> (sin distinguir mayúsculas) activa la verificación.</summary>
    public bool EnableVerify => ParseBool(Properties.Get(KeyEnableVerify));

    /// <summary>Limpieza de archivos no listados. Si la clave falta, se considera desactivada.</summary>
    public bool DeleteUnauthorized => ParseBool(Properties.Get(KeyDeleteUnauthorized));

    /// <summary><c>java.memory</c> convertido a MB (por ejemplo <c>4G</c> → 4096).</summary>
    public int MemoryMb => ParseMemoryMb(JavaMemory) ?? ParseMemoryMb(DefaultMemory)!.Value;

    // ── Utilidades de conversión ─────────────────────────────────────────

    /// <summary>Solo el literal "true" es verdadero; cualquier otro valor es false.</summary>
    public static bool ParseBool(string? value) =>
        value is not null && value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Convierte el formato de <c>-Xmx</c> a MB: <c>4G</c>/<c>4g</c> → 4096, <c>512M</c> → 512,
    /// número sin sufijo → MB. Devuelve null si el valor no es válido o es ≤ 0.
    /// </summary>
    public static int? ParseMemoryMb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim().ToUpperInvariant();
        var multiplier = 1.0;
        if (text.EndsWith("GB", StringComparison.Ordinal) || text.EndsWith("MB", StringComparison.Ordinal))
            text = text[..^1];
        if (text.EndsWith('G'))
        {
            multiplier = 1024;
            text = text[..^1];
        }
        else if (text.EndsWith('M'))
        {
            text = text[..^1];
        }

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return null;
        var mb = number * multiplier;
        return mb > int.MaxValue ? null : (int)mb;
    }

    /// <summary>Convierte MB al formato que se guarda en <c>java.memory</c> (4096 → <c>4G</c>).</summary>
    public static string FormatMemory(int megabytes) =>
        megabytes % 1024 == 0 ? $"{megabytes / 1024}G" : $"{megabytes}M";

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
