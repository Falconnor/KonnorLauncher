using KonnorLauncher.Core.Modrinth;

namespace KonnorLauncher.Core.Stores;

/// <summary>
/// Las tiendas de Explorar y la elegida en Opciones. Busca en la elegida; los proyectos y versiones se piden a la tienda
/// de su identificador (<c>cf:</c>, <c>ftb:</c> o Modrinth), así lo instalado de cualquier tienda sigue funcionando
/// aunque se cambie de tienda. Las versiones de Minecraft siempre las da Modrinth.
/// </summary>
public sealed class ContentStores : IContentStore
{
    public ContentStores(ModrinthClient modrinth, CurseForgeClient curseForge, FtbClient ftb, StoreKind current = StoreKind.Modrinth)
    {
        Modrinth = modrinth;
        CurseForge = curseForge;
        Ftb = ftb;
        Current = Get(current);
    }

    public ModrinthClient Modrinth { get; }
    public CurseForgeClient CurseForge { get; }
    public FtbClient Ftb { get; }

    /// <summary>La tienda elegida.</summary>
    public IContentStore Current { get; private set; }

    /// <summary>Se eligió otra tienda.</summary>
    public event EventHandler? Changed;

    public IContentStore Get(StoreKind kind) => kind switch
    {
        StoreKind.CurseForge => CurseForge,
        StoreKind.Ftb => Ftb,
        _ => Modrinth,
    };

    public void Select(StoreKind kind)
    {
        var store = Get(kind);
        if (ReferenceEquals(store, Current))
            return;
        Current = store;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tienda de un proyecto o una versión por su identificador.</summary>
    public static StoreKind KindOf(string id) =>
        id.StartsWith(CurseForgeClient.Prefix, StringComparison.Ordinal) ? StoreKind.CurseForge
        : id.StartsWith(FtbClient.Prefix, StringComparison.Ordinal) ? StoreKind.Ftb
        : StoreKind.Modrinth;

    public IContentStore ForId(string id) => Get(KindOf(id));

    /// <summary>Valor guardado en Opciones (<c>modrinth</c>, <c>curseforge</c> o <c>ftb</c>; otro = Modrinth).</summary>
    public static StoreKind Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "curseforge" => StoreKind.CurseForge,
        "ftb" => StoreKind.Ftb,
        _ => StoreKind.Modrinth,
    };

    public static string Format(StoreKind kind) => kind switch
    {
        StoreKind.CurseForge => "curseforge",
        StoreKind.Ftb => "ftb",
        _ => "modrinth",
    };

    // ── IContentStore: buscar en la elegida, el resto por identificador ──

    public StoreKind Kind => Current.Kind;
    public string Name => Current.Name;
    public bool Supports(ContentType type) => Current.Supports(type);

    public Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct = default) => Current.SearchAsync(query, ct);

    public Task<ProjectDetails> GetProjectAsync(string projectId, CancellationToken ct = default) =>
        ForId(projectId).GetProjectAsync(projectId, ct);

    public Task<IReadOnlyList<ProjectVersion>> GetVersionsAsync(string projectId, IEnumerable<string>? loaders = null,
        IEnumerable<string>? gameVersions = null, CancellationToken ct = default) =>
        ForId(projectId).GetVersionsAsync(projectId, loaders, gameVersions, ct);

    public Task<ProjectVersion> GetVersionAsync(string versionId, CancellationToken ct = default) =>
        ForId(versionId).GetVersionAsync(versionId, ct);

    public Task<IReadOnlyList<CategoryTag>> GetCategoriesAsync(CancellationToken ct = default) => Current.GetCategoriesAsync(ct);
    public Task<IReadOnlyList<LoaderTag>> GetLoadersAsync(CancellationToken ct = default) => Current.GetLoadersAsync(ct);
    public Task<IReadOnlyList<GameVersionTag>> GetGameVersionsAsync(CancellationToken ct = default) => Modrinth.GetGameVersionsAsync(ct);
}
