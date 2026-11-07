using System.Text.Json.Serialization;

namespace KonnorLauncher.Core.Modrinth;

/// <summary>Tipos de contenido de Modrinth que se pueden explorar.</summary>
public enum ContentType
{
    Modpack,
    Mod,
    ResourcePack,
    DataPack,
    Shader,
}

public static class ContentTypes
{
    /// <summary>Valor de <c>project_type</c> en las búsquedas y en las etiquetas.</summary>
    public static string ProjectType(this ContentType type) => type switch
    {
        ContentType.Modpack => "modpack",
        ContentType.Mod => "mod",
        ContentType.ResourcePack => "resourcepack",
        ContentType.DataPack => "datapack",
        ContentType.Shader => "shader",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Los modpacks crean una instancia; el resto se instala en una existente.</summary>
    public static bool CreatesInstance(this ContentType type) => type is ContentType.Modpack;

    /// <summary>Carpeta de la instancia donde se instala (los data packs van en un mundo).</summary>
    public static string? Folder(this ContentType type) => type switch
    {
        ContentType.Mod => "mods",
        ContentType.ResourcePack => "resourcepacks",
        ContentType.Shader => "shaderpacks",
        _ => null,
    };
}

/// <summary>Resultado de búsqueda (<c>/v2/search</c>).</summary>
public sealed record SearchHit
{
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectType { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Categorías y cargadores (los cargadores también aparecen aquí).</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Categorías que muestra Modrinth (sin los cargadores repetidos).</summary>
    public IReadOnlyList<string> DisplayCategories { get; init; } = [];

    public long Downloads { get; init; }
    public long Follows { get; init; }
    public string? IconUrl { get; init; }
    public DateTimeOffset DateModified { get; init; }
    public string? ClientSide { get; init; }
    public string? ServerSide { get; init; }
    public int? Color { get; init; }
}

public sealed record SearchPage
{
    public IReadOnlyList<SearchHit> Hits { get; init; } = [];
    public int Offset { get; init; }
    public int Limit { get; init; }
    public int TotalHits { get; init; }
}

public sealed record CategoryTag
{
    public string Name { get; init; } = string.Empty;
    public string ProjectType { get; init; } = string.Empty;
    public string Header { get; init; } = string.Empty;
}

public sealed record LoaderTag
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> SupportedProjectTypes { get; init; } = [];
}

public sealed record GameVersionTag
{
    public string Version { get; init; } = string.Empty;
    public string VersionType { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
}

/// <summary>Versión publicada de un proyecto (<c>/v2/project/{id}/version</c>).</summary>
public sealed record ProjectVersion
{
    public string Id { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string VersionNumber { get; init; } = string.Empty;
    public string VersionType { get; init; } = string.Empty;
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public DateTimeOffset DatePublished { get; init; }
    public long Downloads { get; init; }
    public IReadOnlyList<VersionFile> Files { get; init; } = [];
    public IReadOnlyList<VersionDependency> Dependencies { get; init; } = [];

    /// <summary>Archivo principal (o el primero si ninguno está marcado).</summary>
    [JsonIgnore]
    public VersionFile? PrimaryFile => Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
}

public sealed record VersionFile
{
    public string Url { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public bool Primary { get; init; }
    public long Size { get; init; }
    public FileHashes Hashes { get; init; } = new();
}

public sealed record FileHashes
{
    public string? Sha1 { get; init; }
    public string? Sha512 { get; init; }
}

public sealed record VersionDependency
{
    public string? VersionId { get; init; }
    public string? ProjectId { get; init; }
    public string? FileName { get; init; }
    public string DependencyType { get; init; } = string.Empty;
}

/// <summary>Página de un proyecto (<c>/v2/project/{id}</c>): descripción completa, galería, enlaces y detalles.</summary>
public sealed record ProjectDetails
{
    public string Id { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string ProjectType { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Descripción larga en Markdown (puede llevar HTML).</summary>
    public string Body { get; init; } = string.Empty;

    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> AdditionalCategories { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public string? ClientSide { get; init; }
    public string? ServerSide { get; init; }
    public long Downloads { get; init; }
    public long Followers { get; init; }
    public string? IconUrl { get; init; }
    public int? Color { get; init; }
    public DateTimeOffset Published { get; init; }
    public DateTimeOffset Updated { get; init; }
    public ProjectLicense? License { get; init; }
    public string? IssuesUrl { get; init; }
    public string? SourceUrl { get; init; }
    public string? WikiUrl { get; init; }
    public string? DiscordUrl { get; init; }
    public IReadOnlyList<DonationLink> DonationUrls { get; init; } = [];
    public IReadOnlyList<GalleryImage> Gallery { get; init; } = [];
}

public sealed record ProjectLicense
{
    public string Id { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? Url { get; init; }
}

public sealed record DonationLink
{
    public string Platform { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public sealed record GalleryImage
{
    public string Url { get; init; } = string.Empty;
    public string? RawUrl { get; init; }
    public bool Featured { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public long Ordering { get; init; }
}
