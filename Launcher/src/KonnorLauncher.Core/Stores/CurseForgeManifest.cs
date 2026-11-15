using System.IO.Compression;
using System.Text.Json;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Modrinth;

namespace KonnorLauncher.Core.Stores;

/// <summary>Archivo de un modpack de CurseForge: proyecto, archivo y si es obligatorio.</summary>
public sealed record CurseForgeManifestFile(int ProjectId, int FileId, bool Required);

/// <summary>
/// <c>manifest.json</c> de un modpack de CurseForge: versión de Minecraft, cargador (<c>forge-47.2.0</c>), la lista de
/// archivos (por su número; las direcciones se piden a la API) y la carpeta con la configuración (<c>overrides</c>).
/// </summary>
public sealed record CurseForgeManifest(string Minecraft, ModLoader Loader, string? LoaderVersion,
    IReadOnlyList<CurseForgeManifestFile> Files, string Overrides)
{
    public static CurseForgeManifest Read(ZipArchive archive)
    {
        var entry = archive.GetEntry("manifest.json") ?? throw new ModrinthException("El archivo no es un modpack de CurseForge.");
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        return Parse(document.RootElement);
    }

    public static CurseForgeManifest Parse(JsonElement root)
    {
        if (!root.TryGetProperty("minecraft", out var mc) || mc.ValueKind != JsonValueKind.Object
            || !mc.TryGetProperty("version", out var v) || v.GetString() is not { Length: > 0 } minecraft)
            throw new ModrinthException("El modpack no indica su versión de Minecraft.");

        var (loader, loaderVersion) = (ModLoader.Vanilla, (string?)null);
        if (mc.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
        {
            var list = loaders.EnumerateArray().ToList();
            var primary = list.FirstOrDefault(l => l.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True);
            if (primary.ValueKind == JsonValueKind.Undefined && list.Count > 0)
                primary = list[0];
            if (primary.ValueKind == JsonValueKind.Object && primary.TryGetProperty("id", out var id) && id.GetString() is { } text)
                (loader, loaderVersion) = ParseLoader(text);
        }

        var files = new List<CurseForgeManifestFile>();
        if (root.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in array.EnumerateArray())
            {
                if (!file.TryGetProperty("projectID", out var project) || !project.TryGetInt32(out var projectId)
                    || !file.TryGetProperty("fileID", out var fileElement) || !fileElement.TryGetInt32(out var fileId))
                    continue;
                var required = !file.TryGetProperty("required", out var r) || r.ValueKind != JsonValueKind.False;
                files.Add(new CurseForgeManifestFile(projectId, fileId, required));
            }
        }

        var overrides = root.TryGetProperty("overrides", out var o) && o.GetString() is { Length: > 0 } folder ? folder.Trim('/') : "overrides";
        return new CurseForgeManifest(minecraft, loader, loaderVersion, files, overrides);
    }

    /// <summary><c>forge-47.2.0</c> → Forge 47.2.0 (también <c>neoforge-</c>, <c>fabric-</c> y <c>quilt-</c>).</summary>
    public static (ModLoader Loader, string? Version) ParseLoader(string id)
    {
        var dash = id.IndexOf('-');
        if (dash <= 0)
            throw new ModrinthException($"Cargador del modpack no reconocido: {id}");
        var version = id[(dash + 1)..];
        return id[..dash].ToLowerInvariant() switch
        {
            "forge" => (ModLoader.Forge, version),
            "neoforge" => (ModLoader.NeoForge, version),
            "fabric" => (ModLoader.Fabric, version),
            "quilt" => (ModLoader.Quilt, version),
            _ => throw new ModrinthException($"El modpack usa un cargador que el launcher no admite ({id})."),
        };
    }
}
