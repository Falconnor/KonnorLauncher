using System.Text.Json;

namespace KonnorLauncher.Core.Modrinth;

/// <summary>Orden de los resultados (<c>index</c> de la búsqueda).</summary>
public enum SearchSort
{
    Relevance,
    Downloads,
    Follows,
    Newest,
    Updated,
}

/// <summary>Entorno en el que funciona el proyecto.</summary>
public enum ProjectEnvironment
{
    Client,
    Server,
}

/// <summary>
/// Búsqueda en Modrinth con sus filtros. Los <c>facets</c> son una lista de grupos: dentro de un grupo
/// basta con que se cumpla uno (O) y se tienen que cumplir todos los grupos (Y).
/// </summary>
public sealed record SearchQuery
{
    public ContentType Type { get; init; } = ContentType.Mod;
    public string Text { get; init; } = string.Empty;
    public SearchSort Sort { get; init; } = SearchSort.Relevance;
    public int Limit { get; init; } = 20;
    public int Page { get; init; } = 1;

    /// <summary>Categorías que deben tener todas (cada una es un grupo).</summary>
    public IReadOnlyCollection<string> IncludedCategories { get; init; } = [];

    /// <summary>Categorías que no deben tener.</summary>
    public IReadOnlyCollection<string> ExcludedCategories { get; init; } = [];

    /// <summary>Basta con una de estas versiones de Minecraft.</summary>
    public IReadOnlyCollection<string> GameVersions { get; init; } = [];

    /// <summary>Basta con uno de estos cargadores.</summary>
    public IReadOnlyCollection<string> Loaders { get; init; } = [];

    public IReadOnlyCollection<ProjectEnvironment> Environments { get; init; } = [];

    public bool OpenSourceOnly { get; init; }

    /// <summary>
    /// Tipo de proyecto de Modrinth en lugar del de <see cref="Type"/>: <c>plugin</c> para buscar plugins de
    /// Paper, Spigot y Bukkit (un servidor Paper de la sección Hosting).
    /// </summary>
    public string? ProjectTypeOverride { get; init; }

    public const int MaxLimit = 100;

    public int Offset => (Math.Max(1, Page) - 1) * Math.Clamp(Limit, 1, MaxLimit);

    public static string SortIndex(SearchSort sort) => sort switch
    {
        SearchSort.Downloads => "downloads",
        SearchSort.Follows => "follows",
        SearchSort.Newest => "newest",
        SearchSort.Updated => "updated",
        _ => "relevance",
    };

    public List<List<string>> BuildFacets()
    {
        var facets = new List<List<string>> { new() { "project_type:" + (ProjectTypeOverride ?? Type.ProjectType()) } };
        foreach (var category in IncludedCategories)
            facets.Add(["categories:" + category]);
        foreach (var category in ExcludedCategories)
            facets.Add(["categories!=" + category]);
        if (GameVersions.Count > 0)
            facets.Add(GameVersions.Select(v => "versions:" + v).ToList());
        if (Loaders.Count > 0)
            facets.Add(Loaders.Select(l => "categories:" + l).ToList());
        if (Environments.Contains(ProjectEnvironment.Client))
            facets.Add(["client_side:required", "client_side:optional"]);
        if (Environments.Contains(ProjectEnvironment.Server))
            facets.Add(["server_side:required", "server_side:optional"]);
        if (OpenSourceOnly)
            facets.Add(["open_source:true"]);
        return facets;
    }

    /// <summary>Parámetros de <c>/v2/search</c> ya codificados.</summary>
    public string ToQueryString()
    {
        var parts = new List<string>
        {
            "facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(BuildFacets())),
            "index=" + SortIndex(Sort),
            "offset=" + Offset,
            "limit=" + Math.Clamp(Limit, 1, MaxLimit),
        };
        if (!string.IsNullOrWhiteSpace(Text))
            parts.Insert(0, "query=" + Uri.EscapeDataString(Text.Trim()));
        return string.Join('&', parts);
    }
}
