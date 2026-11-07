using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Modrinth;
using KonnorLauncher.Core.Servers;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Una opción de un desplegable (orden, versión, categoría, región, idioma). Value null = «Todas».</summary>
public sealed partial class ServerChoice(string? value, string label, Action<ServerChoice> choose) : ObservableObject
{
    public string? Value { get; } = value;
    public string Label { get; } = label;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Choose() => choose(this);
}

/// <summary>Desplegable de filtro: sus opciones, la elegida y el texto del botón («Versión: 1.21.1»).</summary>
public sealed partial class ServerFilter : ObservableObject
{
    private readonly Action _changed;

    public ServerFilter(string name, Action changed)
    {
        Name = name;
        _changed = changed;
    }

    public string Name { get; }
    public ObservableCollection<ServerChoice> Options { get; } = [];

    [ObservableProperty] private ServerChoice? _selected;

    /// <summary>Su menú está abierto.</summary>
    [ObservableProperty] private bool _isOpen;

    public string? Value => Selected?.Value;
    public string SelectedLabel => Selected?.Label ?? string.Empty;
    public bool IsActive => Selected?.Value is not null;

    /// <summary>Rellena las opciones (la primera es «Todas» si <paramref name="allLabel"/> no es null) y elige <paramref name="selected"/>.</summary>
    public void SetOptions(IEnumerable<(string? Value, string Label)> options, string? allLabel = "Todas", string? selected = null)
    {
        Options.Clear();
        if (allLabel is not null)
            Options.Add(new ServerChoice(null, allLabel, Choose));
        foreach (var (value, label) in options)
            Options.Add(new ServerChoice(value, label, Choose));
        var pick = Options.FirstOrDefault(o => o.Value == selected) ?? Options.FirstOrDefault();
        SetSelected(pick, notify: false);
    }

    private void Choose(ServerChoice choice)
    {
        IsOpen = false;
        if (choice == Selected)
            return;
        SetSelected(choice, notify: true);
    }

    private void SetSelected(ServerChoice? choice, bool notify)
    {
        foreach (var option in Options)
            option.IsSelected = option == choice;
        Selected = choice;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(SelectedLabel));
        OnPropertyChanged(nameof(IsActive));
        if (notify)
            _changed();
    }
}

/// <summary>Servidor del directorio de Modrinth tal como se ve en la lista.</summary>
public sealed partial class ServerCard(ServerListing server) : ObservableObject
{
    public ServerListing Server { get; } = server;
    public string Name => Server.Name;
    /// <summary>En una línea (algunos servidores ponen saltos de línea en la descripción).</summary>
    public string Summary { get; } = string.Join(' ', server.Summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public string Address => Server.Address.ToString();
    public bool IsOnline => Server.IsOnline;
    public string PlayersText => Server.PlayersOnline is { } online
        ? Server.PlayersMax is > 0 and var max ? $"{online:N0} / {max:N0} jugando" : $"{online:N0} jugando"
        : "Sin respuesta";
    public string VersionText => Server.ContentKind == ServerContentKind.Modpack
        ? "Modpack"
        : Server.RecommendedVersion is { } version ? $"Minecraft {version}" : "Vanilla";
    public string? RegionText => ServerText.Region(Server.Region);
    public string LanguagesText => string.Join(" · ", Server.Languages.Take(3).Select(l => l.ToUpperInvariant()));
    public bool HasLanguages => Server.Languages.Count > 0;
    public bool NeedsPremium => !Server.AllowsOfflinePlayers;
    public IReadOnlyList<string> Tags { get; } = server.Categories
        .Where(c => c != ServerDirectoryClient.OfflineModeCategory && !ServerText.IsLoader(c))
        .Take(4).Select(ServerText.Category).ToList();

    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isCopied;

    public bool HasIcon => Icon is not null;
    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));
}

/// <summary>Servidor de «Mis servidores» con su estado en vivo.</summary>
public sealed partial class SavedServerCard(SavedServer saved, ServerAddress address) : ObservableObject
{
    public SavedServer Saved { get; } = saved;
    public ServerAddress ServerAddress { get; } = address;
    public string Name => Saved.Name;
    public string Address => Saved.Address;
    public bool ShowAddress => Name != Address;

    [ObservableProperty] private bool _isChecking = true;
    [ObservableProperty] private bool? _isOnline;
    [ObservableProperty] private string _statusText = "Comprobando…";
    [ObservableProperty] private string? _motd;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isCopied;

    public bool HasIcon => Icon is not null;
    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasIcon));
}

/// <summary>Pestañas de Servidores.</summary>
public enum ServersTab
{
    /// <summary>Servidores que piden cuenta premium (con las cuentas del launcher no se puede entrar).</summary>
    Premium,

    /// <summary>Servidores en modo offline: se entra con las cuentas del launcher.</summary>
    NoPremium,

    /// <summary>Servidores en español, premium o no.</summary>
    Spanish,
    Mine,
}

/// <summary>Pide a la ventana que juegue la instancia y entre directamente al servidor.</summary>
public sealed record ServerPlayRequest(string InstanceId, ServerAddress Address);

/// <summary>
/// Sección Servidores. «Descubrir»: el directorio de servidores de Modrinth con búsqueda, filtros y orden;
/// «Jugar» crea (o reutiliza) la instancia del servidor con su modpack o su versión, lo añade a la lista de
/// servidores del juego y entra directo. «Mis servidores»: direcciones guardadas por el jugador con su
/// estado en vivo; «Jugar» las añade a la instancia seleccionada y entra.
/// </summary>
public sealed partial class ServersViewModel : ObservableObject
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(350);

    private readonly ServerDirectoryClient _directory;
    private readonly ServerStatusClient _status;
    private readonly ServerLibrary _library;
    private readonly ContentInstaller _installer;
    private readonly InstanceStore _store;
    private readonly ModrinthClient _modrinth;
    private readonly IconLoader _icons;
    private readonly ILogger<ServersViewModel> _logger;
    private CancellationTokenSource? _searchCts;
    private int _nextOffset;
    private bool _loaded;
    private bool _savedLoaded;
    private string? _latestRelease;

    public ServersViewModel(ServerDirectoryClient directory, ServerStatusClient status, ServerLibrary library, ContentInstaller installer,
        InstanceStore store, ModrinthClient modrinth, IconLoader icons, ILogger<ServersViewModel> logger)
    {
        _directory = directory;
        _status = status;
        _library = library;
        _installer = installer;
        _store = store;
        _modrinth = modrinth;
        _icons = icons;
        _logger = logger;

        Sort = new ServerFilter("Ordenar por", FiltersChanged);
        Sort.SetOptions([("players", "Más jugadores"), ("plays", "Más jugados"), ("relevance", "Relevancia"), ("newest", "Más nuevos")],
            allLabel: null, selected: "players");
        Version = new ServerFilter("Versión", FiltersChanged);
        Version.SetOptions([]);
        Category = new ServerFilter("Categoría", FiltersChanged);
        Category.SetOptions(ServerText.Categories.Select(c => ((string?)c, ServerText.Category(c))).OrderBy(c => c.Item2, StringComparer.CurrentCulture));
        Region = new ServerFilter("Región", FiltersChanged);
        Region.SetOptions(ServerText.Regions.Select(r => ((string?)r.Key, r.Value)));
        Language = new ServerFilter("Idioma", FiltersChanged);
        Language.SetOptions(ServerText.Languages.Select(l => ((string?)l.Key, l.Value)));
    }

    /// <summary>Se pide jugar (la ventana selecciona la instancia y lanza el juego hacia el servidor).</summary>
    public event EventHandler<ServerPlayRequest>? PlayRequested;

    /// <summary>Se creó una instancia (la ventana recarga la lista).</summary>
    public event EventHandler<string>? InstanceCreated;

    public ObservableCollection<ServerCard> Servers { get; } = [];
    public ObservableCollection<SavedServerCard> SavedServers { get; } = [];

    public ServerFilter Sort { get; }
    public ServerFilter Version { get; }
    public ServerFilter Category { get; }
    public ServerFilter Region { get; }
    public ServerFilter Language { get; }

    /// <summary>Pestaña: No premium al abrir (los servidores a los que se puede entrar con el launcher).</summary>
    [ObservableProperty] private ServersTab _tab = ServersTab.NoPremium;

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private int _total;

    // Mis servidores
    [ObservableProperty] private string _newAddress = string.Empty;
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string? _addError;

    /// <summary>Instancia seleccionada en la barra lateral (a la que se añaden «Mis servidores» al jugar).</summary>
    [ObservableProperty] private InstanceItemViewModel? _targetInstance;

    public bool IsMine => Tab == ServersTab.Mine;
    public bool IsDiscover => !IsMine;

    // Pestañas de la barra superior (RadioButton: al marcar una se cambia de pestaña)
    public bool IsPremiumTab { get => Tab == ServersTab.Premium; set { if (value) Tab = ServersTab.Premium; } }
    public bool IsNoPremiumTab { get => Tab == ServersTab.NoPremium; set { if (value) Tab = ServersTab.NoPremium; } }
    public bool IsSpanishTab { get => Tab == ServersTab.Spanish; set { if (value) Tab = ServersTab.Spanish; } }
    public bool IsMineTab { get => Tab == ServersTab.Mine; set { if (value) Tab = ServersTab.Mine; } }

    /// <summary>En «En español» el idioma ya está elegido.</summary>
    public bool ShowLanguageFilter => Tab != ServersTab.Spanish;

    public string SearchPlaceholder => Tab switch
    {
        ServersTab.Premium => "Buscar servidores premium",
        ServersTab.Spanish => "Buscar servidores en español",
        _ => "Buscar servidores sin premium",
    };
    public bool IsEmpty => !IsLoading && Error is null && Servers.Count == 0;
    public bool HasNoSaved => SavedServers.Count == 0;
    public string TotalText => Total == 1 ? "1 servidor" : $"{Total:N0} servidores";
    public bool HasTarget => TargetInstance is not null;
    public string TargetText => TargetInstance is { } target
        ? $"«Jugar» añade el servidor a la instancia «{target.Name}» y entra."
        : "Selecciona o crea una instancia en la barra lateral para poder jugar.";

    partial void OnTabChanged(ServersTab value)
    {
        foreach (var name in new[] { nameof(IsMine), nameof(IsDiscover), nameof(IsPremiumTab), nameof(IsNoPremiumTab),
                     nameof(IsSpanishTab), nameof(IsMineTab), nameof(ShowLanguageFilter), nameof(SearchPlaceholder) })
            OnPropertyChanged(name);
        if (value == ServersTab.Mine)
            _ = LoadSavedAsync();
        else
            _ = SearchAsync(TimeSpan.Zero);
    }

    partial void OnSearchTextChanged(string value) => _ = SearchAsync(SearchDelay);
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnTotalChanged(int value) => OnPropertyChanged(nameof(TotalText));

    partial void OnTargetInstanceChanged(InstanceItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(TargetText));
    }

    private void FiltersChanged() => _ = SearchAsync(TimeSpan.Zero);

    /// <summary>Al abrir la sección: la primera vez carga el directorio y las versiones del filtro.</summary>
    public async Task OpenAsync()
    {
        if (IsMine)
            await RefreshSavedAsync();
        if (_loaded)
            return;
        _loaded = true;
        _ = LoadVersionsAsync();
        await SearchAsync(TimeSpan.Zero);
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            var tags = await _modrinth.GetGameVersionsAsync();
            var releases = tags.Where(t => t.VersionType == "release").Select(t => t.Version).ToList();
            _latestRelease = releases.FirstOrDefault();
            Version.SetOptions(releases.Select(v => ((string?)v, v)), selected: Version.Value);
        }
        catch (ModrinthException ex)
        {
            _logger.LogInformation(ex, "Sin versiones para el filtro de servidores");
        }
    }

    [RelayCommand]
    private Task RetryAsync() => SearchAsync(TimeSpan.Zero);

    private ServerQuery Query(int offset) => new()
    {
        Text = SearchText,
        Sort = Sort.Value switch
        {
            "plays" => ServerSort.Plays,
            "relevance" => ServerSort.Relevance,
            "newest" => ServerSort.Newest,
            _ => ServerSort.Players,
        },
        GameVersion = Version.Value,
        Category = Category.Value,
        Region = Region.Value,
        Language = Tab == ServersTab.Spanish ? "es" : Language.Value,
        OfflineMode = Tab switch
        {
            ServersTab.Premium => false,
            ServersTab.NoPremium => true,
            _ => null,
        },
        Offset = offset,
    };

    private async Task SearchAsync(TimeSpan delay)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cts.Token);
            IsLoading = true;
            Error = null;
            Servers.Clear();
            HasMore = false;
            _nextOffset = 0;
            var page = await _directory.SearchAsync(Query(0), cts.Token);
            if (!cts.IsCancellationRequested)
                AddPage(page);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (ModrinthException ex)
        {
            if (!cts.IsCancellationRequested)
                Error = ex.Message;
        }
        finally
        {
            if (_searchCts == cts)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoadingMore || !HasMore || _searchCts is not { } cts)
            return;
        IsLoadingMore = true;
        try
        {
            var page = await _directory.SearchAsync(Query(_nextOffset), cts.Token);
            if (!cts.IsCancellationRequested)
                AddPage(page);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (ModrinthException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private void AddPage(ServerPage page)
    {
        Total = page.Total;
        _nextOffset += ServerDirectoryClient.PageSize;
        HasMore = _nextOffset < page.Total && page.Servers.Count > 0;
        foreach (var server in page.Servers)
        {
            // La búsqueda por páginas puede repetir alguno si el orden cambia entre consultas
            if (Servers.Any(s => s.Server.ProjectId == server.ProjectId))
                continue;
            var card = new ServerCard(server);
            Servers.Add(card);
            _ = LoadIconAsync(card);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task LoadIconAsync(ServerCard card) => card.Icon = await _icons.LoadAsync(card.Server.IconUrl);

    // ── Jugar a un servidor del directorio ───────────────────────────────

    [RelayCommand]
    private async Task PlayAsync(ServerCard? card)
    {
        if (card is null || card.IsBusy)
            return;
        card.IsBusy = true;
        card.Error = null;
        card.ProgressText = "Preparando la instancia…";
        try
        {
            // Creado aquí (hilo de la interfaz): los avances de la instalación vuelven a él solos.
            var progress = new Progress<InstallProgress>(p => card.ProgressText = p.Percent is { } percent
                ? $"{p.Message} {percent:0} %"
                : p.Message);
            var instance = await Task.Run(() => InstanceFor(card, progress));
            if (instance is null)
                return;
            card.ProgressText = "Añadiendo el servidor…";
            await Task.Run(() => ServersDat.Add(_store.GetDirectory(instance.Id), card.Name, card.Server.Address));
            PlayRequested?.Invoke(this, new ServerPlayRequest(instance.Id, card.Server.Address));
        }
        catch (Exception ex) when (ex is ModrinthException or IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "No se pudo preparar el servidor {Project}", card.Server.ProjectId);
            card.Error = $"No se pudo preparar: {ex.Message}";
        }
        finally
        {
            card.IsBusy = false;
            card.ProgressText = null;
        }
    }

    /// <summary>
    /// La instancia del servidor: la que se creó antes para él, si sigue existiendo; si no, una nueva con
    /// su modpack (instalado de Modrinth) o con su versión de Minecraft.
    /// </summary>
    private GameInstance? InstanceFor(ServerCard card, IProgress<InstallProgress> progress)
    {
        var server = card.Server;
        if (_library.LinkedInstance(server.ProjectId) is { } linked
            && _store.LoadAll().FirstOrDefault(i => i.Id == linked) is { } existing)
            return existing;

        GameInstance created;
        if (server.ContentKind == ServerContentKind.Modpack && server.ModpackProjectId is { } pack)
        {
            created = _installer.InstallModpackAsync(pack, server.Name, progress, versionId: server.ModpackVersionId)
                .GetAwaiter().GetResult();
        }
        else
        {
            var version = server.RecommendedVersion ?? _latestRelease;
            if (version is null)
            {
                Application.Current.Dispatcher.Invoke(() => card.Error = "El servidor no indica qué versión de Minecraft usa.");
                return null;
            }

            created = _store.Create(new GameInstance { Name = _installer.UniqueName(server.Name), MinecraftVersion = version });
        }

        _library.LinkInstance(server.ProjectId, created.Id);
        Application.Current.Dispatcher.Invoke(() => InstanceCreated?.Invoke(this, created.Id));
        return created;
    }

    [RelayCommand]
    private async Task CopyAddressAsync(object? item)
    {
        var (address, setCopied) = item switch
        {
            ServerCard card => (card.Address, (Action<bool>)(v => card.IsCopied = v)),
            SavedServerCard saved => (saved.Address, v => saved.IsCopied = v),
            _ => (null, null!),
        };
        if (address is null)
            return;
        try
        {
            Clipboard.SetText(address);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return; // el portapapeles lo tiene otro programa
        }

        setCopied(true);
        await Task.Delay(1500);
        setCopied(false);
    }

    [RelayCommand]
    private void OpenOnModrinth(ServerCard? card)
    {
        if (card is null)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(card.Server.PageUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    // ── Mis servidores ───────────────────────────────────────────────────

    private async Task LoadSavedAsync()
    {
        if (_savedLoaded)
        {
            await RefreshSavedAsync();
            return;
        }

        _savedLoaded = true;
        var saved = await Task.Run(_library.Saved);
        SavedServers.Clear();
        foreach (var server in saved)
            AddSavedCard(server);
        OnPropertyChanged(nameof(HasNoSaved));
    }

    private SavedServerCard? AddSavedCard(SavedServer server)
    {
        if (!ServerAddress.TryParse(server.Address, out var address))
            return null;
        var card = new SavedServerCard(server, address);
        SavedServers.Add(card);
        _ = CheckAsync(card);
        return card;
    }

    [RelayCommand]
    private async Task RefreshSavedAsync()
    {
        await Task.WhenAll(SavedServers.Select(CheckAsync).ToList());
    }

    private async Task CheckAsync(SavedServerCard card)
    {
        card.IsChecking = true;
        card.StatusText = "Comprobando…";
        var status = await _status.GetAsync(card.ServerAddress);
        card.IsChecking = false;
        card.IsOnline = status?.Online;
        card.StatusText = status switch
        {
            null => "No se pudo comprobar",
            { Online: false } => "Desconectado",
            { PlayersMax: > 0 } s => $"{s.PlayersOnline:N0} / {s.PlayersMax:N0} jugando" + (s.Version is { } v ? $" · {v}" : string.Empty),
            var s => $"{s.PlayersOnline:N0} jugando" + (s.Version is { } v ? $" · {v}" : string.Empty),
        };
        card.Motd = status?.Motd;
        if (status?.Icon is { } png)
            card.Icon = ToImage(png);
    }

    private static ImageSource? ToImage(byte[] png)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or IOException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    [RelayCommand]
    private void AddServer()
    {
        AddError = null;
        if (!ServerAddress.TryParse(NewAddress, out _))
        {
            AddError = "Escribe la dirección como «servidor.com» o «servidor.com:25565».";
            return;
        }

        try
        {
            var saved = _library.Add(NewAddress, NewName);
            if (SavedServers.FirstOrDefault(s => s.Address == saved.Address) is { } existing)
                SavedServers.Remove(existing);
            AddSavedCard(saved);
            NewAddress = string.Empty;
            NewName = string.Empty;
            OnPropertyChanged(nameof(HasNoSaved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AddError = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveServer(SavedServerCard? card)
    {
        if (card is null)
            return;
        try
        {
            _library.Remove(card.Address);
            SavedServers.Remove(card);
            OnPropertyChanged(nameof(HasNoSaved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            card.Error = ex.Message;
        }
    }

    /// <summary>«Jugar» en Mis servidores: se añade a la instancia seleccionada y se entra.</summary>
    [RelayCommand]
    private async Task PlaySavedAsync(SavedServerCard? card)
    {
        if (card is null || card.IsBusy || TargetInstance is not { } target)
            return;
        card.IsBusy = true;
        card.Error = null;
        try
        {
            await Task.Run(() => ServersDat.Add(target.Directory, card.Name, card.ServerAddress));
            PlayRequested?.Invoke(this, new ServerPlayRequest(target.Id, card.ServerAddress));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            card.Error = $"No se pudo añadir el servidor: {ex.Message}";
        }
        finally
        {
            card.IsBusy = false;
        }
    }

    /// <summary>Esc: no hay nada que cerrar en esta sección.</summary>
    public static bool CloseTopmost() => false;
}

/// <summary>Textos de los servidores: categorías, regiones e idiomas en español.</summary>
public static class ServerText
{
    /// <summary>Categorías de servidores de Modrinth (sin «offline-mode», que tiene su interruptor).</summary>
    public static readonly string[] Categories =
    [
        "adventure-mode", "anarchy", "battle-royale", "bedwars", "bosses", "classes", "competitive", "creative-mode",
        "creator-community", "crossplay", "custom-content", "dungeons", "economy", "factions", "gens", "hardcore-mode",
        "keep-inventory", "kitpvp", "lifesteal", "media", "microgames", "minigames", "mmo", "network", "oneblock", "op",
        "parkour", "personal-worlds", "plots", "pokemon", "prison", "pve", "pvp", "questing", "racing", "recording-smp",
        "roleplay", "rpg", "skyblock", "smp", "social", "survival-mode", "teams", "technical", "towns", "vanilla-like",
        "whitelisted", "world-resets",
    ];

    private static readonly Dictionary<string, string> CategoryLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adventure-mode"] = "Modo aventura", ["anarchy"] = "Anarquía", ["battle-royale"] = "Battle royale", ["bedwars"] = "BedWars",
        ["bosses"] = "Jefes", ["classes"] = "Clases", ["competitive"] = "Competitivo", ["creative-mode"] = "Modo creativo",
        ["creator-community"] = "Comunidad de creadores", ["crossplay"] = "Multiplataforma", ["custom-content"] = "Contenido propio",
        ["dungeons"] = "Mazmorras", ["economy"] = "Economía", ["factions"] = "Facciones", ["gens"] = "Gens",
        ["hardcore-mode"] = "Modo extremo", ["keep-inventory"] = "Conservar inventario", ["kitpvp"] = "KitPvP",
        ["lifesteal"] = "Lifesteal", ["media"] = "Medios", ["microgames"] = "Microjuegos", ["minigames"] = "Minijuegos",
        ["mmo"] = "MMO", ["network"] = "Red de servidores", ["oneblock"] = "OneBlock", ["op"] = "OP", ["parkour"] = "Parkour",
        ["personal-worlds"] = "Mundos personales", ["plots"] = "Parcelas", ["pokemon"] = "Pokémon", ["prison"] = "Prisión",
        ["pve"] = "PvE", ["pvp"] = "PvP", ["questing"] = "Misiones", ["racing"] = "Carreras", ["recording-smp"] = "SMP de grabación",
        ["roleplay"] = "Rol", ["rpg"] = "RPG", ["skyblock"] = "SkyBlock", ["smp"] = "SMP", ["social"] = "Social",
        ["survival-mode"] = "Supervivencia", ["teams"] = "Equipos", ["technical"] = "Técnico", ["towns"] = "Pueblos",
        ["vanilla-like"] = "Estilo vanilla", ["whitelisted"] = "Con lista blanca", ["world-resets"] = "Reinicios de mundo",
    };

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Regions =
    [
        new("europe", "Europa"), new("us_east", "EE. UU. (este)"), new("us_west", "EE. UU. (oeste)"),
        new("south_america", "Sudamérica"), new("asia", "Asia"), new("australia", "Oceanía"), new("russia", "Rusia"),
        new("middle_east", "Oriente Medio"), new("africa", "África"),
    ];

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Languages =
    [
        new("es", "Español"), new("en", "Inglés"), new("pt", "Portugués"), new("fr", "Francés"), new("de", "Alemán"),
        new("it", "Italiano"), new("ru", "Ruso"), new("pl", "Polaco"), new("nl", "Neerlandés"), new("tr", "Turco"),
        new("uk", "Ucraniano"), new("zh", "Chino"), new("ja", "Japonés"), new("ko", "Coreano"),
    ];

    private static readonly HashSet<string> Loaders = new(StringComparer.OrdinalIgnoreCase)
        { "fabric", "forge", "neoforge", "quilt", "vanilla", "paper", "spigot", "bukkit", "purpur", "folia", "velocity", "bungeecord" };

    public static bool IsLoader(string category) => Loaders.Contains(category);

    public static string Category(string name) => CategoryLabels.TryGetValue(name, out var label) ? label : BrowseText.Category(name);

    /// <summary>Región en español; una desconocida, legible («north_africa» → «North africa»).</summary>
    public static string? Region(string? region) =>
        string.IsNullOrWhiteSpace(region) ? null
            : Regions.FirstOrDefault(r => r.Key == region).Value
              ?? char.ToUpperInvariant(region[0]) + region[1..].Replace('_', ' ');
}
