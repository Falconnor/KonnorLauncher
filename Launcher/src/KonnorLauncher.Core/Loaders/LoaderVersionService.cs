using System.Collections.Concurrent;
using System.Text.Json;
using CmlLib.Core.Installer.Forge.Versions;
using KonnorLauncher.Core.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Loaders;

/// <summary>Una versión de un cargador de mods.</summary>
public sealed record LoaderVersionInfo(string Version, bool IsStable);

/// <summary>
/// Versiones disponibles de cada cargador para una versión de Minecraft, de la más nueva a la más
/// antigua. Fuentes oficiales:
/// <list type="bullet">
/// <item>Fabric: <c>meta.fabricmc.net</c> (indica cuáles son estables).</item>
/// <item>Quilt: <c>meta.quiltmc.org</c> (las betas llevan <c>-beta</c> en el nombre).</item>
/// <item>Forge y NeoForge: las listas de CmlLib.Core.Installer.Forge (Forge marca la recomendada).</item>
/// </list>
/// </summary>
public sealed class LoaderVersionService
{
    /// <summary>Nombre del HttpClient para las consultas de cargadores.</summary>
    public const string HttpClientName = "loaders";

    public const string FabricMeta = "https://meta.fabricmc.net/v2";
    public const string QuiltMeta = "https://meta.quiltmc.org/v3";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(ModLoader, string), IReadOnlyList<LoaderVersionInfo>> _cache = new();

    public LoaderVersionService(IHttpClientFactory httpFactory, ILogger<LoaderVersionService>? logger = null)
    {
        _httpFactory = httpFactory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Versiones del cargador para <paramref name="minecraftVersion"/>. Lista vacía = el cargador no
    /// existe para esa versión. Lanza <see cref="HttpRequestException"/> si no hay conexión.
    /// </summary>
    public async Task<IReadOnlyList<LoaderVersionInfo>> GetVersionsAsync(ModLoader loader, string minecraftVersion, CancellationToken ct = default)
    {
        if (loader == ModLoader.Vanilla || string.IsNullOrWhiteSpace(minecraftVersion))
            return [];
        if (_cache.TryGetValue((loader, minecraftVersion), out var cached))
            return cached;

        var http = _httpFactory.CreateClient(HttpClientName);
        var mc = Uri.EscapeDataString(minecraftVersion);
        IReadOnlyList<LoaderVersionInfo> versions = loader switch
        {
            ModLoader.Fabric => ParseFabricLike(await GetStringOrEmptyAsync(http, $"{FabricMeta}/versions/loader/{mc}", ct), quilt: false),
            ModLoader.Quilt => ParseFabricLike(await GetStringOrEmptyAsync(http, $"{QuiltMeta}/versions/loader/{mc}", ct), quilt: true),
            ModLoader.Forge => await GetForgeAsync(http, minecraftVersion),
            ModLoader.NeoForge => await GetNeoForgeAsync(http, minecraftVersion),
            _ => [],
        };

        _cache[(loader, minecraftVersion)] = versions;
        _logger.LogInformation("{Loader} para {Minecraft}: {Count} versiones", loader, minecraftVersion, versions.Count);
        return versions;
    }

    /// <summary>Versión que corresponde al canal: la primera estable (o la primera si no hay) o la última.</summary>
    public static string? Pick(IReadOnlyList<LoaderVersionInfo> versions, LoaderChannel channel) => channel switch
    {
        LoaderChannel.Latest => versions.FirstOrDefault()?.Version,
        _ => (versions.FirstOrDefault(v => v.IsStable) ?? versions.FirstOrDefault())?.Version,
    };

    // ── Fuentes ──────────────────────────────────────────────────────────

    /// <summary>
    /// Lista de <c>/versions/loader/{mc}</c> de Fabric o Quilt: <c>[{ "loader": { "version", "stable"? } }]</c>.
    /// Quilt no tiene el campo <c>stable</c>: las versiones de prueba llevan beta, pre o rc.
    /// </summary>
    public static List<LoaderVersionInfo> ParseFabricLike(string json, bool quilt)
    {
        var result = new List<LoaderVersionInfo>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("loader", out var loader) || !loader.TryGetProperty("version", out var versionElement))
                continue;
            var version = versionElement.GetString();
            if (string.IsNullOrEmpty(version))
                continue;
            var stable = quilt
                ? IsReleaseName(version)
                : loader.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True;
            result.Add(new LoaderVersionInfo(version, stable));
        }

        return result;
    }

    /// <summary>Sin "beta", "alpha", "pre" ni "rc" en el nombre.</summary>
    public static bool IsReleaseName(string version) =>
        !new[] { "beta", "alpha", "pre", "rc" }.Any(tag => version.Contains(tag, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<LoaderVersionInfo>> GetForgeAsync(HttpClient http, string minecraftVersion)
    {
        try
        {
            var versions = await new ForgeVersionLoader(http).GetForgeVersions(minecraftVersion).ConfigureAwait(false);
            return versions.Select(v => new LoaderVersionInfo(v.ForgeVersionName, v.IsRecommendedVersion)).ToList();
        }
        catch (Exception ex) when (ex is not HttpRequestException and not OperationCanceledException)
        {
            // La lista de Forge es una página web: si la versión no existe, no hay página que leer.
            _logger.LogInformation(ex, "Forge no tiene versiones para {Minecraft}", minecraftVersion);
            return [];
        }
    }

    private static async Task<IReadOnlyList<LoaderVersionInfo>> GetNeoForgeAsync(HttpClient http, string minecraftVersion)
    {
        var versions = await new NeoForgeVersionLoader(http).GetNeoForgeVersions(minecraftVersion).ConfigureAwait(false);
        return versions.Select(v => new LoaderVersionInfo(v.NeoForgeVersionName, IsReleaseName(v.NeoForgeVersionName))).ToList();
    }

    /// <summary>GET que devuelve "" si el recurso no existe (404); otros errores se propagan.</summary>
    private static async Task<string> GetStringOrEmptyAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return string.Empty;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
