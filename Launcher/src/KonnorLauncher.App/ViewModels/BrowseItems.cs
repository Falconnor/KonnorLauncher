using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Modrinth;

namespace KonnorLauncher.App.ViewModels;

/// <summary>
/// Pestaña de tipo de contenido (Modpacks, Mods...). Se elige al marcarla, con el ratón, el teclado o
/// la accesibilidad de Windows.
/// </summary>
public sealed partial class BrowseTab(ContentType type, string label, string icon, Action<BrowseTab> selected) : ObservableObject
{
    public ContentType Type { get; } = type;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    [ObservableProperty] private bool _isActive;

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
            selected(this);
    }
}

/// <summary>Opción de un filtro en forma de pastilla (entorno, versión, cargador, contenido incluido).</summary>
public sealed partial class FilterOption(string value, string label, Action changed) : ObservableObject
{
    public string Value { get; } = value;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isActive;

    partial void OnIsActiveChanged(bool value) => changed();

    [RelayCommand]
    private void Toggle() => IsActive = !IsActive;
}

public enum CategoryState
{
    None,
    Included,
    Excluded,
}

/// <summary>Categoría del panel de filtros: cada clic pasa de nada → incluida → excluida → nada.</summary>
public sealed partial class CategoryFilter(string value, string label, Action changed) : ObservableObject
{
    public string Value { get; } = value;
    public string Label { get; } = label;
    [ObservableProperty] private CategoryState _state;

    public bool IsIncluded => State == CategoryState.Included;
    public bool IsExcluded => State == CategoryState.Excluded;

    partial void OnStateChanged(CategoryState value)
    {
        OnPropertyChanged(nameof(IsIncluded));
        OnPropertyChanged(nameof(IsExcluded));
        changed();
    }

    [RelayCommand]
    private void Cycle() => State = State switch
    {
        CategoryState.None => CategoryState.Included,
        CategoryState.Included => CategoryState.Excluded,
        _ => CategoryState.None,
    };
}

/// <summary>Filtro activo mostrado como chip en la barra de herramientas (con su ✕).</summary>
public sealed class FilterChip(string label, bool excluded, Action remove)
{
    public string Label { get; } = label;
    public bool IsExcluded { get; } = excluded;
    public IRelayCommand RemoveCommand { get; } = new RelayCommand(remove);
}

/// <summary>Botón de la paginación: un número o "…".</summary>
public sealed class PageItem(int? number, bool isCurrent, IRelayCommand<int> go)
{
    public int? Number { get; } = number;
    public bool IsEllipsis => Number is null;
    public bool IsCurrent { get; } = isCurrent;
    public string Text => Number?.ToString(CultureInfo.InvariantCulture) ?? "…";
    public IRelayCommand<int> GoCommand { get; } = go;
}

/// <summary>Opción del menú "Ordenar por".</summary>
public sealed record SortOption(SearchSort Value, string Label);

/// <summary>Instancia de destino de las instalaciones.</summary>
public sealed class TargetOption(GameInstance instance)
{
    public GameInstance Instance { get; } = instance;
    public string Name => Instance.Name;
    public string Summary => Instance.Summary;
}

/// <summary>Etiqueta de una tarjeta: entorno, categoría, cargador (con su color) o "+N".</summary>
public sealed record CardTag(string Text, string Kind);

public enum CardState
{
    Available,
    Installing,
    Installed,
}

/// <summary>Tarjeta de un proyecto en la lista de resultados.</summary>
public sealed partial class ProjectCard : ObservableObject
{
    private const int MaxTags = 5;

    public ProjectCard(SearchHit hit, ContentType type, Func<ProjectCard, Task> install)
    {
        Hit = hit;
        Type = type;
        InstallCommand = new AsyncRelayCommand(() => install(this), () => State == CardState.Available && BlockReason is null);
        Initial = hit.Title.Length > 0 ? hit.Title[..1].ToUpperInvariant() : "?";
        var rgb = hit.Color ?? 0x27AE60;
        IconBackground = new SolidColorBrush(Color.FromArgb(0x55, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        IconBackground.Freeze();
        Tags = BuildTags(hit, type);
    }

    public SearchHit Hit { get; }
    public ContentType Type { get; }
    public string Title => Hit.Title;
    public string AuthorText => $"por {Hit.Author}";
    public string Description => Hit.Description;
    public string Initial { get; }
    public Brush IconBackground { get; }
    public IReadOnlyList<CardTag> Tags { get; }
    public string DownloadsText => BrowseText.Compact(Hit.Downloads);
    public string FollowsText => BrowseText.Compact(Hit.Follows);
    public string UpdatedText => BrowseText.Ago(Hit.DateModified);
    public IAsyncRelayCommand InstallCommand { get; }

    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private CardState _state;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressKnown;
    [ObservableProperty] private string? _blockReason;

    public bool HasIcon => Icon is not null;
    public bool IsInstalling => State == CardState.Installing;

    public string ActionText => State switch
    {
        CardState.Installing => IsProgressKnown ? $"{Progress:0} %" : "Instalando…",
        CardState.Installed => Type.CreatesInstance() ? "Instancia creada" : "Instalado",
        _ => Type.CreatesInstance() ? "Crear instancia" : "Instalar",
    };

    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ActionText));
    partial void OnIsProgressKnownChanged(bool value) => OnPropertyChanged(nameof(ActionText));
    partial void OnBlockReasonChanged(string? value) => InstallCommand.NotifyCanExecuteChanged();

    partial void OnStateChanged(CardState value)
    {
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsInstalling));
        InstallCommand.NotifyCanExecuteChanged();
    }

    private static List<CardTag> BuildTags(SearchHit hit, ContentType type)
    {
        var tags = new List<CardTag>();
        if (type is ContentType.Mod or ContentType.Modpack && BrowseText.EnvironmentLabel(hit.ClientSide, hit.ServerSide) is { } env)
            tags.Add(new CardTag(env, "env"));
        // Solo los cargadores que usa un jugador (no los de servidores de plugins, ni "minecraft").
        var loaders = hit.Categories.Where(BrowseText.IsPlayerLoader).ToList();
        tags.AddRange(hit.DisplayCategories.Where(c => !BrowseText.IsLoader(c)).Select(c => new CardTag(BrowseText.Category(c), "category")));
        tags.AddRange(loaders.Select(l => new CardTag(BrowseText.Loader(l), l)));
        if (tags.Count <= MaxTags)
            return tags;
        // Las de cargador se conservan (dicen dónde funciona); se recortan categorías.
        var kept = tags.Where(t => t.Kind is "env" or "category").Take(Math.Max(1, MaxTags - loaders.Count)).ToList();
        kept.AddRange(tags.Where(t => t.Kind is not ("env" or "category")).Take(MaxTags - kept.Count));
        kept.Add(new CardTag($"+{tags.Count - kept.Count}", "more"));
        return kept;
    }
}

/// <summary>Textos de la página: números compactos, fechas relativas y nombres de categorías y cargadores.</summary>
public static class BrowseText
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public static readonly HashSet<string> Loaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt", "liteloader", "modloader", "rift", "babric", "legacy-fabric", "ornithe",
        "iris", "optifine", "canvas", "vanilla", "datapack", "minecraft", "bukkit", "spigot", "paper", "purpur", "folia",
        "sponge", "velocity", "bungeecord", "waterfall", "nilloader", "java-agent", "bta-babric", "geyser",
    };

    public static bool IsLoader(string name) => Loaders.Contains(name);

    private static readonly HashSet<string> ServerLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bukkit", "spigot", "paper", "purpur", "folia", "sponge", "velocity", "bungeecord", "waterfall", "geyser", "java-agent", "minecraft",
    };

    /// <summary>Cargadores que interesan a un jugador: no los de servidores de plugins.</summary>
    public static bool IsPlayerLoader(string name) => IsLoader(name) && !ServerLoaders.Contains(name);

    public static string Compact(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000.0).ToString("0.#", Es) + " M",
        >= 10_000 => (value / 1_000.0).ToString("0.#", Es) + " k",
        _ => value.ToString("N0", Es),
    };

    public static string Ago(DateTimeOffset date)
    {
        var span = DateTimeOffset.Now - date;
        if (span.TotalMinutes < 1) return "ahora mismo";
        if (span.TotalHours < 1) return $"hace {(int)span.TotalMinutes} min";
        if (span.TotalDays < 1) return $"hace {(int)span.TotalHours} h";
        if (span.TotalDays < 2) return "ayer";
        if (span.TotalDays < 31) return $"hace {(int)span.TotalDays} días";
        if (span.TotalDays < 365)
        {
            var months = Math.Max(1, (int)(span.TotalDays / 30.4));
            return months == 1 ? "hace 1 mes" : $"hace {months} meses";
        }

        var years = (int)(span.TotalDays / 365.25);
        return years <= 1 ? "hace 1 año" : $"hace {years} años";
    }

    public static string? EnvironmentLabel(string? client, string? server)
    {
        var onClient = client is "required" or "optional";
        var onServer = server is "required" or "optional";
        return (onClient, onServer) switch
        {
            (true, true) => "Cliente y servidor",
            (true, false) => "Cliente",
            (false, true) => "Servidor",
            _ => null,
        };
    }

    public static string Loader(string name) => name.ToLowerInvariant() switch
    {
        "neoforge" => "NeoForge",
        "optifine" => "OptiFine",
        "datapack" => "Data pack",
        "minecraft" => "Minecraft",
        "legacy-fabric" => "Legacy Fabric",
        "bungeecord" => "BungeeCord",
        _ => Capitalize(name),
    };

    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adventure"] = "Aventura", ["cursed"] = "Maldito", ["decoration"] = "Decoración", ["economy"] = "Economía",
        ["equipment"] = "Equipo", ["food"] = "Comida", ["game-mechanics"] = "Mecánicas", ["library"] = "Librería",
        ["magic"] = "Magia", ["management"] = "Gestión", ["minigame"] = "Minijuego", ["mobs"] = "Criaturas",
        ["optimization"] = "Optimización", ["social"] = "Social", ["storage"] = "Almacenamiento", ["technology"] = "Tecnología",
        ["transportation"] = "Transporte", ["utility"] = "Utilidad", ["worldgen"] = "Generación de mundos",
        ["challenging"] = "Desafiante", ["combat"] = "Combate", ["kitchen-sink"] = "De todo", ["lightweight"] = "Ligero",
        ["multiplayer"] = "Multijugador", ["quests"] = "Misiones", ["blocks"] = "Bloques", ["core-shaders"] = "Shaders básicos",
        ["entities"] = "Entidades", ["environment"] = "Entorno", ["font"] = "Fuente", ["gui"] = "Interfaz",
        ["items"] = "Objetos", ["locale"] = "Idioma", ["models"] = "Modelos", ["simplistic"] = "Simple", ["sounds"] = "Sonidos",
        ["themed"] = "Temático", ["tweaks"] = "Retoques", ["vanilla-like"] = "Estilo vanilla", ["realistic"] = "Realista",
        ["fantasy"] = "Fantasía", ["semi-realistic"] = "Semirrealista", ["cartoon"] = "Dibujo animado", ["atmosphere"] = "Atmósfera",
        ["bloom"] = "Resplandor", ["colored-lighting"] = "Luz de colores", ["foliage"] = "Vegetación", ["path-tracing"] = "Path tracing",
        ["pbr"] = "PBR", ["reflections"] = "Reflejos", ["shadows"] = "Sombras", ["high"] = "Alto", ["medium"] = "Medio",
        ["low"] = "Bajo", ["potato"] = "Muy bajo", ["screenshot"] = "Capturas", ["audio"] = "Audio", ["animated"] = "Animado",
        ["8x-"] = "8x o menos", ["512x+"] = "512x o más",
    };

    public static string Category(string name) =>
        Categories.TryGetValue(name, out var label) ? label : Capitalize(name.Replace("minecraft_server_", string.Empty).Replace('-', ' ').Replace('_', ' '));

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Es) + text[1..];
}
