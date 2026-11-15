namespace KonnorLauncher.Core.Modrinth;

/// <summary>Tienda de la que Explorar saca el contenido.</summary>
public enum StoreKind
{
    Modrinth,
    CurseForge,
    Ftb,
}

/// <summary>
/// Una tienda de contenido (Modrinth, CurseForge o FTB). Todas devuelven los mismos modelos, así Explorar, la página
/// del proyecto y el instalador funcionan igual con cualquiera. Los identificadores de proyectos y versiones de las
/// tiendas que no son Modrinth llevan delante su prefijo (<c>cf:</c>, <c>ftb:</c>): así no se mezclan en el índice
/// de lo instalado y se sabe a qué tienda pedir cada uno.
/// </summary>
public interface IContentStore
{
    StoreKind Kind { get; }
    string Name { get; }

    /// <summary>Si la tienda tiene este tipo de contenido (FTB solo tiene modpacks).</summary>
    bool Supports(ContentType type);

    Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct = default);
    Task<ProjectDetails> GetProjectAsync(string projectId, CancellationToken ct = default);

    /// <summary>Versiones de un proyecto, de la más reciente a la más antigua.</summary>
    Task<IReadOnlyList<ProjectVersion>> GetVersionsAsync(string projectId, IEnumerable<string>? loaders = null,
        IEnumerable<string>? gameVersions = null, CancellationToken ct = default);

    Task<ProjectVersion> GetVersionAsync(string versionId, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryTag>> GetCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LoaderTag>> GetLoadersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GameVersionTag>> GetGameVersionsAsync(CancellationToken ct = default);
}
