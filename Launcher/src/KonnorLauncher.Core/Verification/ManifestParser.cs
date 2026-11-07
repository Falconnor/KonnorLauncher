using System.Text.Json;
using System.Text.RegularExpressions;
using KonnorLauncher.Core.Instances;

namespace KonnorLauncher.Core.Verification;

/// <summary>
/// Parseo y validación de <c>client_manifest.json</c>. Todo se valida ANTES de tocar el disco.
/// </summary>
public static partial class ManifestParser
{
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[0-9a-fA-F]{128}$")]
    private static partial Regex Sha512Regex();

    /// <summary>Versiones de Minecraft y del cargador: se usan en URLs y nombres de carpeta.</summary>
    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$")]
    private static partial Regex VersionRegex();

    /// <summary>
    /// Parsea el texto JSON. Un JSON mal formado se considera un fallo del servidor
    /// (<c>servidor no responde</c>).
    /// </summary>
    public static ClientManifest Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new VerificationException(VerificationException.ServerNotResponding, ex);
        }

        using (document)
            return Parse(document.RootElement);
    }

    /// <summary>Valida la estructura completa del manifest.</summary>
    public static ClientManifest Parse(JsonElement root)
    {
        // Sección "files": obligatoria y debe ser un objeto.
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("files", out var filesElement)
            || filesElement.ValueKind != JsonValueKind.Object)
        {
            throw new VerificationException("El manifest no contiene una sección files válida");
        }

        string? clientVersion = null;
        if (root.TryGetProperty("client_version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String)
            clientVersion = versionElement.GetString();

        var files = ParseFiles(filesElement, blockName: null);
        var blocks = ParseAssetBlocks(root);
        // Política del servidor (solo cuenta un booleano; si falta, null).
        return new ClientManifest(clientVersion, files, blocks,
            ReadFlag(root, "delete_unauthorized"), ReadFlag(root, "always_online"), ReadFlag(root, "released"),
            ReadFlag(root, "lock_server_url"), ReadFlag(root, "protect_packs"), ParseGame(root), ParseAutoJoin(root));
    }

    /// <summary>
    /// <c>auto_join: true</c> con <c>server_address</c> válida (<c>host</c> o <c>host:puerto</c>) → dirección
    /// normalizada. Si falta o no es válida, null: se juega sin entrar a ningún servidor (no se rechaza el manifest).
    /// </summary>
    private static string? ParseAutoJoin(JsonElement root)
    {
        if (ReadFlag(root, "auto_join") != true)
            return null;
        return Servers.ServerAddress.TryParse(ReadString(root, "server_address"), out var address) ? address.ToString() : null;
    }

    /// <summary>
    /// Sección opcional <c>minecraft</c>: <c>{ "version", "loader", "loader_version" }</c>. Si está pero no
    /// es válida, el manifest se rechaza: sin ella el servidor no publica los archivos del juego.
    /// </summary>
    private static ManifestGame? ParseGame(JsonElement root)
    {
        if (!root.TryGetProperty("minecraft", out var game) || game.ValueKind == JsonValueKind.Null)
            return null;
        if (game.ValueKind != JsonValueKind.Object)
            throw new VerificationException("Sección minecraft inválida");

        var version = ReadString(game, "version");
        if (version is null || !VersionRegex().IsMatch(version))
            throw new VerificationException("Versión de Minecraft inválida en el manifest");

        ModLoader? loader = ReadString(game, "loader")?.ToLowerInvariant() switch
        {
            null or "vanilla" => ModLoader.Vanilla,
            "fabric" => ModLoader.Fabric,
            "quilt" => ModLoader.Quilt,
            "forge" => ModLoader.Forge,
            "neoforge" => ModLoader.NeoForge,
            _ => null,
        };
        if (loader is null)
            throw new VerificationException("Cargador de mods desconocido en el manifest");
        if (loader == ModLoader.Vanilla)
            return new ManifestGame(version, ModLoader.Vanilla, null);

        var loaderVersion = ReadString(game, "loader_version");
        if (loaderVersion is null || !VersionRegex().IsMatch(loaderVersion))
            throw new VerificationException("Versión del cargador inválida en el manifest");
        return new ManifestGame(version, loader.Value, loaderVersion);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? ReadFlag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    /// <summary>
    /// Sección opcional <c>asset_blocks</c>: si falta, está vacía o no es un objeto → lista vacía
    /// (compatibilidad con manifests antiguos).
    /// </summary>
    private static List<AssetBlock> ParseAssetBlocks(JsonElement root)
    {
        var blocks = new List<AssetBlock>();
        if (!root.TryGetProperty("asset_blocks", out var blocksElement) || blocksElement.ValueKind != JsonValueKind.Object)
            return blocks;

        var byName = new Dictionary<string, AssetBlock>(StringComparer.Ordinal);
        foreach (var property in blocksElement.EnumerateObject())
        {
            var name = property.Name;
            var meta = property.Value;
            if (meta.ValueKind != JsonValueKind.Object)
                throw new VerificationException($"Metadatos inválidos para bloque {name}");

            // El nombre del bloque se usa como archivo en la carpeta temporal, así que se valida como una ruta más.
            ManifestPath.Normalize(name);

            var sha = ReadSha(meta) ?? throw new VerificationException($"SHA-256 inválido para bloque {name}");
            var size = ReadSize(meta) ?? throw new VerificationException($"Tamaño inválido para bloque {name}");

            if (!meta.TryGetProperty("files", out var blockFiles) || blockFiles.ValueKind != JsonValueKind.Object)
                throw new VerificationException($"Sección files inválida en bloque {name}");

            byName[name] = new AssetBlock(name, sha, size, ParseFiles(blockFiles, name));
        }

        blocks.AddRange(byName.Values);
        return blocks;
    }

    /// <summary>Parsea un objeto <c>{ ruta: { sha256, size } }</c>. Claves repetidas: gana la última.</summary>
    private static List<ManifestFile> ParseFiles(JsonElement filesElement, string? blockName)
    {
        var suffix = blockName is null ? string.Empty : $" en {blockName}";
        var result = new Dictionary<string, ManifestFile>(StringComparer.Ordinal);
        foreach (var property in filesElement.EnumerateObject())
        {
            var rawPath = property.Name;
            var meta = property.Value;
            if (meta.ValueKind != JsonValueKind.Object)
                throw new VerificationException($"Metadatos inválidos para {rawPath}{suffix}");

            var (relative, url) = ManifestPath.Normalize(rawPath);
            var sha = ReadSha(meta) ?? throw new VerificationException($"SHA-256 inválido para {rawPath}{suffix}");
            var size = ReadSize(meta) ?? throw new VerificationException($"Tamaño inválido para {rawPath}{suffix}");
            // SHA-512 opcional (para buscarlo en Modrinth): si no es válido se ignora, no es lo que se comprueba.
            var sha512 = ReadString(meta, "sha512") is { } text && Sha512Regex().IsMatch(text) ? text.ToLowerInvariant() : null;
            result[rawPath] = new ManifestFile(relative, url, sha, size, sha512);
        }

        return [.. result.Values];
    }

    /// <summary>SHA-256 de 64 hex normalizado a mayúsculas, o null si no es válido.</summary>
    private static string? ReadSha(JsonElement meta) =>
        meta.TryGetProperty("sha256", out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && Sha256Regex().IsMatch(text)
            ? text.ToUpperInvariant()
            : null;

    /// <summary>Tamaño: número entero ≥ 0 (se rechazan booleanos, decimales y cadenas), o null.</summary>
    private static long? ReadSize(JsonElement meta) =>
        meta.TryGetProperty("size", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var size)
        && !value.GetRawText().Contains('.')
        && !value.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase)
        && size >= 0
            ? size
            : null;
}
