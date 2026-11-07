using System.Text.Json;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Loaders;
using KonnorLauncher.Core.Versions;

namespace KonnorLauncher.Core.Hosting;

/// <summary>
/// Versiones disponibles para crear un servidor. Las de Minecraft salen de la lista de Mojang (la
/// misma del editor de instancias) y las de los cargadores, de <see cref="LoaderVersionService"/>.
/// Paper tiene su propia lista y sus builds (API Fill v3 de PaperMC).
/// </summary>
public sealed class ServerSoftwareCatalog
{
    public const string PaperApi = "https://fill.papermc.io/v3/projects/paper";

    private readonly IHttpClientFactory _httpFactory;
    private readonly VersionService _versions;
    private readonly LoaderVersionService _loaders;

    public ServerSoftwareCatalog(IHttpClientFactory httpFactory, VersionService versions, LoaderVersionService loaders)
    {
        _httpFactory = httpFactory;
        _versions = versions;
        _loaders = loaders;
    }

    /// <summary>Versiones de Minecraft (la más reciente primero). Lista vacía si no hay conexión.</summary>
    public async Task<IReadOnlyList<string>> GetMinecraftVersionsAsync(ServerSoftware software, CancellationToken ct = default)
    {
        if (software == ServerSoftware.Paper)
        {
            var http = _httpFactory.CreateClient(LoaderVersionService.HttpClientName);
            return ParsePaperVersions(await http.GetStringAsync(PaperApi, ct).ConfigureAwait(false));
        }

        return _versions.RemoteReleases is { Count: > 0 } cached
            ? cached
            : await _versions.FetchRemoteReleasesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Versiones del cargador (o builds de Paper) para una versión de Minecraft, la más nueva primero.
    /// Vanilla no tiene. Lista vacía = no existe para esa versión.
    /// </summary>
    public async Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(ServerSoftware software, string minecraftVersion, CancellationToken ct = default)
    {
        switch (software)
        {
            case ServerSoftware.Vanilla:
                return [];
            case ServerSoftware.Paper:
            {
                var http = _httpFactory.CreateClient(LoaderVersionService.HttpClientName);
                using var response = await http.GetAsync($"{PaperApi}/versions/{Uri.EscapeDataString(minecraftVersion)}/builds", ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return [];
                response.EnsureSuccessStatusCode();
                return ParsePaperBuilds(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                    .Select(b => new LoaderVersionInfo(b.Build, b.Stable))
                    .ToList();
            }
            default:
                return await _loaders.GetVersionsAsync(ToLoader(software), minecraftVersion, ct).ConfigureAwait(false);
        }
    }

    public static ModLoader ToLoader(ServerSoftware software) => software switch
    {
        ServerSoftware.Fabric => ModLoader.Fabric,
        ServerSoftware.Quilt => ModLoader.Quilt,
        ServerSoftware.Forge => ModLoader.Forge,
        ServerSoftware.NeoForge => ModLoader.NeoForge,
        _ => ModLoader.Vanilla,
    };

    // ── Paper (Fill v3) ──────────────────────────────────────────────────

    /// <summary>
    /// <c>{"versions": {"1.21": ["1.21.11", "1.21.11-rc3", ...], ...}}</c>: grupos y versiones ya vienen de la
    /// más nueva a la más antigua. Se quitan las de prueba (pre, rc...).
    /// </summary>
    public static List<string> ParsePaperVersions(string json)
    {
        var result = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("versions", out var groups) || groups.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var group in groups.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var entry in group.Value.EnumerateArray())
            {
                if (entry.GetString() is { Length: > 0 } version && LoaderVersionService.IsReleaseName(version))
                    result.Add(version);
            }
        }

        return result;
    }

    /// <summary>Un build de Paper: número, canal estable, descarga y su SHA-256.</summary>
    public sealed record PaperBuild(string Build, bool Stable, string? Url, string? Sha256, string? FileName);

    /// <summary>Builds de <c>/versions/{v}/builds</c> (el más nuevo primero).</summary>
    public static List<PaperBuild> ParsePaperBuilds(string json)
    {
        var result = new List<PaperBuild>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var build in doc.RootElement.EnumerateArray())
        {
            if (!build.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
                continue;
            var stable = build.TryGetProperty("channel", out var channel)
                         && string.Equals(channel.GetString(), "STABLE", StringComparison.OrdinalIgnoreCase);
            string? url = null, sha = null, name = null;
            if (build.TryGetProperty("downloads", out var downloads)
                && downloads.ValueKind == JsonValueKind.Object
                && downloads.TryGetProperty("server:default", out var server))
            {
                url = server.TryGetProperty("url", out var u) ? u.GetString() : null;
                name = server.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (server.TryGetProperty("checksums", out var sums) && sums.TryGetProperty("sha256", out var s))
                    sha = s.GetString();
            }

            result.Add(new PaperBuild(id.ToString(System.Globalization.CultureInfo.InvariantCulture), stable, url, sha, name));
        }

        return result.OrderByDescending(b => int.Parse(b.Build, System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }
}
