using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Progress;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Pestañas de un servidor abierto.</summary>
public enum HostedServerTab
{
    Console,
    Settings,
    Players,
    Content,
    Publish,
}

/// <summary>Línea de la consola, con su nivel para colorearla.</summary>
public sealed record ConsoleLine(string Text, ConsoleLevel Level)
{
    public static ConsoleLine From(string text) => new(text,
        text.StartsWith("> ", StringComparison.Ordinal) ? ConsoleLevel.Command
        : text.StartsWith("[Konnor]", StringComparison.Ordinal) ? ConsoleLevel.Launcher
        : text.Contains("/ERROR]", StringComparison.Ordinal) || text.Contains("/FATAL]", StringComparison.Ordinal) || text.Contains("Exception", StringComparison.Ordinal) ? ConsoleLevel.Error
        : text.Contains("/WARN]", StringComparison.Ordinal) ? ConsoleLevel.Warning
        : ConsoleLevel.Info);
}

public enum ConsoleLevel
{
    Info,
    Warning,
    Error,
    Command,
    Launcher,
}

/// <summary>Un jugador del servidor: los que entraron alguna vez, con un punto verde si está conectado.</summary>

/// <summary>Archivo de la carpeta de mods o plugins.</summary>
public sealed record ContentFile(string Name, string Path, string SizeText, bool IsEnabled);

/// <summary>
/// Un servidor alojado: su fila en «Mis servidores» y su página (consola, configuración, jugadores,
/// contenido y publicación del cliente).
/// </summary>
public sealed partial class HostedServerViewModel : ObservableObject
{
    private const int MaxConsoleLines = 1500;

    private readonly HostingService _hosting;
    private readonly ConfirmViewModel _confirm;
    private readonly IDialogService _dialogs;
    private readonly ILogger _logger;
    private readonly ServerRunner _runner;
    private readonly ClientPublisher _publisher;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _flush;
    private readonly DispatcherTimer _uptime;
    private readonly DispatcherTimer _ping;
    private bool _measuringPing;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private CancellationTokenSource? _work;

    public HostedServerViewModel(HostedServer model, HostingService hosting, ConfirmViewModel confirm, IDialogService dialogs, ILogger logger)
    {
        Model = model;
        _hosting = hosting;
        _confirm = confirm;
        _dialogs = dialogs;
        _logger = logger;
        _runner = hosting.GetRunner(model.Id);
        _publisher = hosting.GetPublisher(model.Id);

        // La consola se vuelca a la vista por tandas: un servidor arrancando escribe cientos de líneas por segundo.
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => FlushConsole(), _dispatcher) { IsEnabled = false };
        _uptime = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnPropertyChanged(nameof(UptimeText)), _dispatcher) { IsEnabled = false };
        // Calidad de la conexión de los conectados: se mide cada 5 s mientras haya alguno.
        _ping = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) => await MeasurePingsAsync(), _dispatcher) { IsEnabled = false };
        foreach (var line in _runner.Lines.TakeLast(MaxConsoleLines))
            Console.Add(ConsoleLine.From(line));
        _runner.LineReceived += (_, line) =>
        {
            _pending.Enqueue(line);
            _dispatcher.BeginInvoke(() => _flush.IsEnabled = true);
        };
        _runner.StateChanged += (_, _) => _dispatcher.BeginInvoke(OnRunnerStateChanged);
        _runner.PlayersChanged += (_, _) => _dispatcher.BeginInvoke(RefreshPlayers);
        // Estado de la publicación (cada archivo servido lo cambia): no toca lo que se esté editando.
        _publisher.Changed += (_, _) => _dispatcher.BeginInvoke(RefreshPublishStatus);

        Difficulty = new ServerFilter("Dificultad", () => { });
        Difficulty.SetOptions([("peaceful", "Pacífica"), ("easy", "Fácil"), ("normal", "Normal"), ("hard", "Difícil")], allLabel: null);
        GameMode = new ServerFilter("Modo de juego", () => { });
        GameMode.SetOptions([("survival", "Supervivencia"), ("creative", "Creativo"), ("adventure", "Aventura"), ("spectator", "Espectador")], allLabel: null);

        LoadSettings();
        RefreshPlayers();
        RefreshPublish();
        OnRunnerStateChanged();
    }

    public HostedServer Model { get; private set; }
    public string Id => Model.Id;
    public string Directory => _hosting.Store.GetDirectory(Model.Id);

    /// <summary>Se pide confirmar y eliminar (lo gestiona la lista).</summary>
    public event EventHandler? DeleteRequested;

    /// <summary>«Añadir mod»: abrir Mods de Explorar para instalar en este servidor.</summary>
    public event EventHandler<ServerInstallTarget>? AddModsRequested;

    // ══════════════════════════════════════════════════════════════════
    // FILA Y CABECERA
    // ══════════════════════════════════════════════════════════════════

    public string Name => Model.Name;
    public string Initial => Model.Name.Length > 0 ? Model.Name[..1].ToUpperInvariant() : "?";
    public ServerSoftware Software => Model.Software;
    public string SoftwareName => HostedServer.SoftwareName(Model.Software);

    public string Summary
    {
        get
        {
            var text = Model.Summary;
            if (!string.IsNullOrWhiteSpace(Model.LoaderVersion))
                text += Model.Software == ServerSoftware.Paper ? $" · build {Model.LoaderVersion}" : $" · {Model.LoaderVersion}";
            return text + " · " + SystemMemory.Format(Model.MemoryMb);
        }
    }

    public ServerState State => _runner.State;
    public bool IsRunning => State == ServerState.Running;
    public bool IsStarting => State == ServerState.Starting;
    public bool IsStopping => State == ServerState.Stopping;
    public bool IsAlive => State != ServerState.Stopped;
    public bool IsStopped => State == ServerState.Stopped;

    public string StateText => IsBusy ? ProgressText : State switch
    {
        ServerState.Starting => "Iniciando…",
        ServerState.Running => "En línea",
        ServerState.Stopping => "Deteniendo…",
        _ => Model.Installed ? "Detenido" : "Sin instalar",
    };

    public string UptimeText => IsRunning && _runner.StartedAt is { } started
        ? "Activo " + FormatSpan(DateTimeOffset.Now - started)
        : string.Empty;

    public int Port => ServerPropertiesFile.Load(PropertiesPath).Port;
    public int MaxPlayersCount => ServerPropertiesFile.Load(PropertiesPath).GetInt("max-players", 20);
    public string PlayersText => $"{_runner.Players.Count} / {MaxPlayersCount} jugadores";

    /// <summary>Dirección para los jugadores de la misma red (siempre se conoce): la IP fijada en server-ip o la de este equipo.</summary>
    public string LanAddress => $"{BoundIp ?? NetworkAccess.GetLanIp() ?? "localhost"}{PortSuffix(Port)}";

    /// <summary>IP fijada en <c>server-ip</c> (null = el servidor escucha en todas las del equipo).</summary>
    private string? BoundIp => ServerPropertiesFile.Load(PropertiesPath).Get("server-ip") is { Length: > 0 } ip ? ip.Trim() : null;

    [ObservableProperty] private string? _publicAddress;
    [ObservableProperty] private bool _isCopied;
    [ObservableProperty] private bool _isLanCopied;

    /// <summary>Instalando, iniciando o reinstalando.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private string _progressText = string.Empty;

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressKnown;
    [ObservableProperty] private string? _error;

    // ══════════════════════════════════════════════════════════════════
    // PESTAÑAS
    // ══════════════════════════════════════════════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConsoleTab), nameof(IsSettingsTab), nameof(IsPlayersTab), nameof(IsContentTab), nameof(IsPublishTab))]
    private HostedServerTab _tab = HostedServerTab.Console;

    public bool IsConsoleTab { get => Tab == HostedServerTab.Console; set { if (value) Tab = HostedServerTab.Console; } }
    public bool IsSettingsTab { get => Tab == HostedServerTab.Settings; set { if (value) Tab = HostedServerTab.Settings; } }
    public bool IsPlayersTab { get => Tab == HostedServerTab.Players; set { if (value) Tab = HostedServerTab.Players; } }
    public bool IsContentTab { get => Tab == HostedServerTab.Content; set { if (value) Tab = HostedServerTab.Content; } }
    public bool IsPublishTab { get => Tab == HostedServerTab.Publish; set { if (value) Tab = HostedServerTab.Publish; } }

    partial void OnTabChanged(HostedServerTab value)
    {
        if (value == HostedServerTab.Content)
            RefreshContent();
        if (value == HostedServerTab.Players)
        {
            ProfileView = null;
            RefreshPlayers();
        }
        if (value == HostedServerTab.Settings)
            LoadSettings();
    }

    // ══════════════════════════════════════════════════════════════════
    // INICIAR / DETENER
    // ══════════════════════════════════════════════════════════════════

    private bool CanStart() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsAlive)
            return;
        Error = null;
        if (!ServerInstaller.IsEulaAccepted(Directory))
        {
            var accept = await _confirm.ShowAsync("EULA de Minecraft",
                "Para iniciar un servidor hay que aceptar el EULA de Minecraft (aka.ms/MinecraftEULA). ¿Lo aceptas?",
                "Aceptar e iniciar", isDanger: false);
            if (!accept)
                return;
            ServerInstaller.AcceptEula(Directory);
        }

        IsBusy = true;
        ProgressText = "Preparando Java…";
        _work = new CancellationTokenSource();
        try
        {
            await Task.Run(() => _hosting.StartAsync(Model, new Progress<LauncherProgress>(OnProgress), _work.Token));
            Tab = HostedServerTab.Console;
            OnPropertyChanged(nameof(Model));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "No se pudo iniciar el servidor {Id}", Id);
            Error = ex is HttpRequestException ? "No se pudo descargar Java: revisa la conexión." : ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
            IsProgressKnown = false;
            OnRunnerStateChanged();
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsAlive && !_publisher.IsRunning)
            return;
        await _hosting.StopAsync(Id);
        OnRunnerStateChanged();
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (IsAlive)
            await _hosting.StopAsync(Id);
        await StartAsync();
    }

    [RelayCommand]
    private async Task KillAsync()
    {
        var ok = await _confirm.ShowAsync("Forzar cierre",
            "El servidor se cerrará sin guardar el mundo: se pierde lo que haya pasado desde el último guardado automático.",
            "Forzar cierre", isDanger: true);
        if (ok)
            _runner.Kill();
    }

    private void OnRunnerStateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanControlPlayer));
        OnPropertyChanged(nameof(ControlHint));
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(IsStopping));
        OnPropertyChanged(nameof(IsAlive));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(UptimeText));
        _uptime.IsEnabled = IsRunning;
        if (State == ServerState.Stopped && _runner.LastExitCode is { } code && code != 0 && !IsBusy && Error is null && _runner.StartedAt is not null)
            Error = $"El servidor se cerró con un error (código {code}). Revisa la consola.";
        RefreshPublishStatus();
    }

    private void OnProgress(LauncherProgress p)
    {
        ProgressText = p.Message;
        IsProgressKnown = p.Percent is not null;
        ProgressValue = p.Percent ?? 0;
    }

    /// <summary>Instala (o reinstala) el software del servidor; lo usa la creación y «Reinstalar».</summary>
    public async Task InstallAsync(bool acceptEula)
    {
        IsBusy = true;
        Error = null;
        ProgressText = "Preparando…";
        _work = new CancellationTokenSource();
        var model = Model.Clone();
        try
        {
            await Task.Run(() => _hosting.Installer.InstallAsync(model, Directory, acceptEula, new Progress<LauncherProgress>(OnProgress), _work.Token));
            model.Installed = true;
            _hosting.Store.Save(model);
            Model = model;
            OnPropertyChanged(nameof(Summary));
        }
        catch (OperationCanceledException)
        {
            Error = "Instalación cancelada.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogWarning(ex, "No se pudo instalar el servidor {Id}", Id);
            Error = ex is HttpRequestException ? "No se pudo descargar el servidor: revisa la conexión." : ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsProgressKnown = false;
            OnPropertyChanged(nameof(StateText));
        }
    }

    [RelayCommand]
    private void CancelWork() => _work?.Cancel();

    [RelayCommand]
    private async Task ReinstallAsync()
    {
        if (IsAlive)
        {
            Error = "Detén el servidor antes de reinstalarlo.";
            return;
        }

        var ok = await _confirm.ShowAsync("Reinstalar el servidor",
            $"Se vuelve a descargar {Model.Summary}. El mundo, los mods, los plugins y la configuración se conservan.",
            "Reinstalar", isDanger: false);
        if (ok)
            await InstallAsync(acceptEula: false);
    }

    [RelayCommand]
    private void Delete() => DeleteRequested?.Invoke(this, EventArgs.Empty);

    // ══════════════════════════════════════════════════════════════════
    // CONSOLA
    // ══════════════════════════════════════════════════════════════════

    public ObservableCollection<ConsoleLine> Console { get; } = [];

    /// <summary>Se añadieron líneas (la vista baja hasta el final si ya estaba abajo).</summary>
    public event EventHandler? ConsoleAppended;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommandCommand))]
    private string _commandText = string.Empty;

    private readonly List<string> _history = [];
    private int _historyIndex;

    private bool CanSend() => CommandText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void SendCommand()
    {
        var text = CommandText.Trim();
        if (!_runner.SendCommand(text))
        {
            Error = "El servidor no está en marcha.";
            return;
        }

        _history.Remove(text);
        _history.Add(text);
        _historyIndex = _history.Count;
        CommandText = string.Empty;
    }

    /// <summary>Flechas arriba y abajo en el campo de comandos: comandos anteriores.</summary>
    public void HistoryStep(int delta)
    {
        if (_history.Count == 0)
            return;
        _historyIndex = Math.Clamp(_historyIndex + delta, 0, _history.Count);
        CommandText = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
    }

    [RelayCommand]
    private void ClearConsole() => Console.Clear();

    private void FlushConsole()
    {
        _flush.IsEnabled = false;
        var added = false;
        while (_pending.TryDequeue(out var line))
        {
            Console.Add(ConsoleLine.From(line));
            added = true;
        }

        while (Console.Count > MaxConsoleLines)
            Console.RemoveAt(0);
        if (added)
            ConsoleAppended?.Invoke(this, EventArgs.Empty);
    }

    // ══════════════════════════════════════════════════════════════════
    // JUGADORES
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Todos los que entraron alguna vez; los conectados, primero.</summary>
    public ObservableCollection<PlayerEntry> Players { get; } = [];
    public bool HasPlayers => Players.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayerActionCommand))]
    private string _playerName = string.Empty;

    [RelayCommand]
    private void RefreshPlayers()
    {
        var online = _runner.Players.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<(string Name, Guid Uuid)> known;
        try
        {
            known = PlayerHistory.LoadEntries(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            known = [];
        }

        // Resumen de cada uno (vida, hambre, nivel…) del último guardado del mundo.
        var uuids = known.ToDictionary(k => k.Name, k => k.Uuid, StringComparer.OrdinalIgnoreCase);
        var lastPing = Players.Where(p => p.IsOnline).ToDictionary(p => p.Name, p => p.PingMs, StringComparer.OrdinalIgnoreCase);
        Players.Clear();
        foreach (var name in known.Select(k => k.Name).Union(online, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(online.Contains)
                     .ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var profile = uuids.TryGetValue(name, out var uuid) ? ReadProfile(name, uuid) : null;
            Players.Add(new PlayerEntry(name, online.Contains(name), profile) { PingMs = lastPing.GetValueOrDefault(name) });
        }

        _ping.IsEnabled = online.Count > 0;
        if (online.Count > 0)
            _ = MeasurePingsAsync();

        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(PlayersText));
        RefreshWhitelist();
    }

    /// <summary>Latencia hasta la IP de cada conectado (la de su línea «logged in» en la consola).</summary>
    private async Task MeasurePingsAsync()
    {
        if (_measuringPing)
            return;
        _measuringPing = true;
        try
        {
            var online = Players.Where(p => p.IsOnline).ToList();
            var results = await Task.WhenAll(online.Select(p => ConnectionQuality.MeasureAsync(_runner.AddressOf(p.Name))));
            for (var i = 0; i < online.Count; i++)
                online[i].PingMs = results[i];
        }
        finally
        {
            _measuringPing = false;
        }
    }

    private PlayerProfile? ReadProfile(string name, Guid uuid)
    {
        try
        {
            return PlayerProfileReader.Read(Directory, name, uuid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ── Listas: todos los jugadores, lista blanca y baneados ─────────────

    /// <summary>Lista que se ve: <c>all</c>, <c>whitelist</c> o <c>banned</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllPlayers), nameof(ShowWhitelist), nameof(ShowBanned))]
    private string _playerList = "all";

    public bool ShowAllPlayers { get => PlayerList == "all"; set { if (value) PlayerList = "all"; } }
    public bool ShowWhitelist { get => PlayerList == "whitelist"; set { if (value) PlayerList = "whitelist"; } }
    public bool ShowBanned { get => PlayerList == "banned"; set { if (value) PlayerList = "banned"; } }

    public ObservableCollection<WhitelistEntry> WhitelistEntries { get; } = [];
    public bool HasWhitelistEntries => WhitelistEntries.Count > 0;
    public string WhitelistTabText => $"Lista blanca ({WhitelistEntries.Count})";

    public ObservableCollection<WhitelistEntry> BannedEntries { get; } = [];
    public bool HasBannedEntries => BannedEntries.Count > 0;
    public string BannedTabText => $"Baneados ({BannedEntries.Count})";

    /// <summary>Aviso si la lista existe pero no se aplica (white-list=false en server.properties).</summary>
    public string? WhitelistNotice => ServerPropertiesFile.Load(PropertiesPath).GetBool("white-list", false)
        ? null
        : "La lista blanca está desactivada: entra cualquiera. Actívala en Configuración → Reglas.";

    partial void OnPlayerListChanged(string value) => RefreshWhitelist();

    [RelayCommand]
    private void RefreshWhitelist()
    {
        WhitelistEntries.Clear();
        foreach (var entry in WhitelistFile.Load(Directory))
            WhitelistEntries.Add(entry);
        BannedEntries.Clear();
        foreach (var entry in BannedPlayersFile.Load(Directory))
            BannedEntries.Add(entry);
        OnPropertyChanged(nameof(HasWhitelistEntries));
        OnPropertyChanged(nameof(WhitelistTabText));
        OnPropertyChanged(nameof(WhitelistNotice));
        OnPropertyChanged(nameof(HasBannedEntries));
        OnPropertyChanged(nameof(BannedTabText));
        NotifyProfileLists();
    }

    // ── Perfil: lista blanca, baneo y operador (cada botón añade o quita según esté o no) ──

    public bool ProfileWhitelisted => ProfileView is { } v && PlayerListFile.Contains(WhitelistFile.PathFor(Directory), v.Name);
    public bool ProfileBanned => ProfileView is { } v && PlayerListFile.Contains(BannedPlayersFile.PathFor(Directory), v.Name);
    public bool ProfileOp => ProfileView is { } v && PlayerListFile.Contains(OpsFile.PathFor(Directory), v.Name);

    public string ProfileWhitelistText => ProfileWhitelisted ? "Quitar de la lista blanca" : "Añadir a la lista blanca";
    public string ProfileBanText => ProfileBanned ? "Desbanear" : "Banear";
    public string ProfileOpText => ProfileOp ? "Quitar operador" : "Hacer operador";

    private void NotifyProfileLists()
    {
        OnPropertyChanged(nameof(ProfileWhitelisted));
        OnPropertyChanged(nameof(ProfileBanned));
        OnPropertyChanged(nameof(ProfileOp));
        OnPropertyChanged(nameof(ProfileWhitelistText));
        OnPropertyChanged(nameof(ProfileBanText));
        OnPropertyChanged(nameof(ProfileOpText));
    }

    [RelayCommand]
    private Task ToggleProfileWhitelistAsync() => ToggleProfileListAsync(ProfileWhitelisted, "whitelist add", "whitelist remove",
        WhitelistFile.PathFor(Directory), (name, uuid) => WhitelistFile.Add(Directory, name, uuid), name => WhitelistFile.Remove(Directory, name));

    [RelayCommand]
    private async Task ToggleProfileBanAsync()
    {
        if (ProfileView is not { } view)
            return;
        // Banear echa al jugador si está dentro: se confirma. Desbanear no.
        if (!ProfileBanned && !await _confirm.ShowAsync($"Banear a {view.Name}", "No podrá volver a entrar a este servidor hasta que lo desbanees.", "Banear", isDanger: true))
            return;
        await ToggleProfileListAsync(ProfileBanned, "ban", "pardon", BannedPlayersFile.PathFor(Directory),
            (name, uuid) => BannedPlayersFile.Add(Directory, name, uuid, DateTimeOffset.Now), name => BannedPlayersFile.Remove(Directory, name));
    }

    [RelayCommand]
    private Task ToggleProfileOpAsync() => ToggleProfileListAsync(ProfileOp, "op", "deop",
        OpsFile.PathFor(Directory), (name, uuid) => OpsFile.Add(Directory, name, uuid), name => OpsFile.Remove(Directory, name));

    /// <summary>
    /// Añade o quita al jugador del perfil de una lista. En marcha, con el comando (el servidor tiene la lista en memoria
    /// y reescribe el archivo: se espera a verlo cambiar); detenido, editando el archivo.
    /// </summary>
    private async Task ToggleProfileListAsync(bool isIn, string addCommand, string removeCommand, string file,
        Func<string, Guid, bool> addOffline, Func<string, bool> removeOffline)
    {
        if (ProfileView is not { } view)
            return;
        var name = view.Name;
        try
        {
            if (IsAlive)
            {
                _runner.SendCommand($"{(isIn ? removeCommand : addCommand)} {name}");
                for (var i = 0; i < 20 && PlayerListFile.Contains(file, name) == isIn; i++)
                    await Task.Delay(100);
            }
            else if (isIn)
            {
                removeOffline(name);
            }
            else
            {
                addOffline(name, view.Profile.Uuid);
            }

            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Error = $"No se pudo cambiar a {name}: {ex.Message}";
        }

        RefreshWhitelist();
    }

    /// <summary>Retira a un jugador de la lista blanca (<c>/whitelist remove</c> en marcha; detenido, el archivo).</summary>
    [RelayCommand]
    private Task RemoveFromWhitelistAsync(WhitelistEntry? entry) =>
        RemoveFromListAsync(entry, "whitelist remove", WhitelistFile.PathFor(Directory), "la lista blanca");

    /// <summary>Levanta el baneo de un jugador (<c>/pardon</c> en marcha; detenido, el archivo).</summary>
    [RelayCommand]
    private Task PardonAsync(WhitelistEntry? entry) =>
        RemoveFromListAsync(entry, "pardon", BannedPlayersFile.PathFor(Directory), "los baneados");

    private async Task RemoveFromListAsync(WhitelistEntry? entry, string command, string file, string listName)
    {
        if (entry is null)
            return;
        try
        {
            if (IsAlive)
            {
                // El servidor tiene la lista en memoria: el comando la cambia y reescribe el archivo.
                _runner.SendCommand($"{command} {entry.Name}");
                for (var i = 0; i < 20 && PlayerListFile.Load(file).Any(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)); i++)
                    await Task.Delay(100);
            }
            else
            {
                PlayerListFile.Remove(file, entry.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Error = $"No se pudo quitar a {entry.Name} de {listName}: {ex.Message}";
        }

        RefreshWhitelist();
    }

    // ── Perfil de un jugador ─────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayerProfileOpen), nameof(CanControlPlayer), nameof(ControlHint))]
    private PlayerProfileViewModel? _profileView;

    [ObservableProperty] private bool _isLoadingProfile;

    public bool IsPlayerProfileOpen => ProfileView is not null;

    /// <summary>Las acciones (dañar, curar, teletransportar…) son comandos: el servidor en marcha y el jugador dentro.</summary>
    public bool CanControlPlayer => ProfileView is { IsOnline: true } && IsRunning;

    public string? ControlHint => ProfileView is null || CanControlPlayer ? null
        : !IsRunning ? "Inicia el servidor para usar las acciones sobre el jugador."
        : "El jugador tiene que estar conectado para usar las acciones.";

    // Campos de las acciones: niveles de experiencia y destino del teletransporte.
    [ObservableProperty] private string _xpLevelsText = string.Empty;
    [ObservableProperty] private string _teleportX = string.Empty;
    [ObservableProperty] private string _teleportY = string.Empty;
    [ObservableProperty] private string _teleportZ = string.Empty;
    public ServerFilter TeleportDimension { get; } = CreateDimensionFilter();

    /// <summary>Modo de juego del jugador: al elegir otro se aplica con <c>/gamemode</c>.</summary>
    public ServerFilter PlayerGameMode => _playerGameMode ??= CreateGameModeFilter();
    private ServerFilter? _playerGameMode;

    private ServerFilter CreateGameModeFilter()
    {
        ServerFilter? filter = null;
        filter = new ServerFilter("Modo de juego", () => _ = PlayerCommandAsync(filter!.Value));
        filter.SetOptions(GameModes, allLabel: null);
        return filter;
    }

    private static readonly (string? Value, string Label)[] GameModes =
        [("survival", "Supervivencia"), ("creative", "Creativo"), ("adventure", "Aventura"), ("spectator", "Espectador")];

    private static ServerFilter CreateDimensionFilter()
    {
        var filter = new ServerFilter("Dimensión", () => { });
        filter.SetOptions([("minecraft:overworld", "Mundo normal"), ("minecraft:the_nether", "Nether"), ("minecraft:the_end", "End")], allLabel: null);
        return filter;
    }

    /// <summary>Al abrir o actualizar el perfil: los campos parten de su estado actual.</summary>
    partial void OnProfileViewChanged(PlayerProfileViewModel? value)
    {
        NotifyProfileLists();
        if (value?.Profile is not { } profile)
            return;
        XpLevelsText = profile.XpLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PlayerGameMode.SetOptions(GameModes, allLabel: null, GameModes[Math.Clamp(profile.GameMode, 0, 3)].Value);
        if (profile.Position is { } pos)
        {
            TeleportX = Math.Floor(pos.X).ToString(System.Globalization.CultureInfo.InvariantCulture);
            TeleportY = Math.Floor(pos.Y).ToString(System.Globalization.CultureInfo.InvariantCulture);
            TeleportZ = Math.Floor(pos.Z).ToString(System.Globalization.CultureInfo.InvariantCulture);
            TeleportDimension.SetOptions(TeleportDimension.Options.Select(o => (o.Value, o.Label)).ToList(), allLabel: null, pos.Dimension);
        }
    }

    /// <summary>
    /// Abre el perfil. Si el jugador está conectado, antes se pide al servidor que guarde (<c>save-all</c>) para ver
    /// su estado actual y no el del último guardado automático.
    /// </summary>
    [RelayCommand]
    private async Task OpenPlayerAsync(PlayerEntry? entry)
    {
        if (entry?.Profile is not { } profile)
            return;
        ProfileView = new PlayerProfileViewModel(profile, entry.IsOnline);
        if (entry.IsOnline && IsRunning)
            await ReloadProfileAsync();
    }

    /// <summary>Guarda el mundo (<c>save-all</c>), espera a que se escriba el archivo del jugador y vuelve a leerlo.</summary>
    private async Task ReloadProfileAsync()
    {
        if (ProfileView is not { } view)
            return;
        var profile = view.Profile;
        var file = PlayerProfileReader.FindDataFile(Directory, profile.Uuid);
        var before = file is null ? DateTime.MinValue : File.GetLastWriteTimeUtc(file);
        IsLoadingProfile = true;
        try
        {
            _runner.SendCommand("save-all");
            for (var i = 0; i < 30 && file is not null && File.GetLastWriteTimeUtc(file) == before; i++)
                await Task.Delay(100);
            await Task.Delay(150); // que termine de escribirse
            if (ProfileView?.Profile.Uuid == profile.Uuid && ReadProfile(profile.Name, profile.Uuid) is { } fresh)
                ProfileView = new PlayerProfileViewModel(fresh, view.IsOnline);
        }
        finally
        {
            IsLoadingProfile = false;
        }
    }

    /// <summary>
    /// Acción del perfil. Minecraft no tiene comandos para curar, dar hambre o aire, así que se usan efectos con el
    /// resultado exacto: regeneración II 3 s = 2 de vida (un corazón); saturación (instantánea) de nivel N+1 = N+1 de hambre
    /// y el doble de saturación; respiración acuática = el aire se recupera.
    /// </summary>
    [RelayCommand]
    private async Task PlayerCommandAsync(string? action)
    {
        if (ProfileView is not { } view || !CanControlPlayer)
            return;
        var name = view.Name;
        string? command = action switch
        {
            "damage" => $"damage {name} 1",
            "heal" => $"effect give {name} minecraft:regeneration 3 1 true",
            "kill" => $"kill {name}",
            "feed" => $"effect give {name} minecraft:saturation 1 5 true",      // +6 de hambre (3 muslos)
            "saturate" => $"effect give {name} minecraft:saturation 1 4 true",  // +10 de saturación (5 muslos)
            "air" => $"effect give {name} minecraft:water_breathing 5 0 true",
            "xp-add" => $"xp add {name} 21 points",                              // 3 botellas (7 puntos de media)
            "xp-set" => int.TryParse(XpLevelsText.Trim(), out var levels) && levels is >= 0 and <= 21863 ? $"xp set {name} {levels} levels" : null,
            "teleport" => TeleportCommand(name),
            "survival" or "creative" or "adventure" or "spectator" => $"gamemode {action} {name}",
            _ => null,
        };
        if (command is null)
        {
            Error = action switch
            {
                "xp-set" => "Niveles: un número entre 0 y 21863.",
                "teleport" => "Coordenadas: números enteros o decimales (la Y entre -64 y 320 en el mundo normal).",
                _ => null,
            };
            return;
        }

        Error = null;
        _runner.SendCommand(command);
        // Los efectos tardan unos ticks (la regeneración, 3 s): se espera antes de volver a leer su estado.
        await Task.Delay(action is "heal" ? 3200 : action is "air" ? 2000 : 400);
        await ReloadProfileAsync();
    }

    private string? TeleportCommand(string name)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        bool Parse(string text, out double value) =>
            double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, culture, out value) && Math.Abs(value) <= 30_000_000;
        if (!Parse(TeleportX, out var x) || !Parse(TeleportY, out var y) || !Parse(TeleportZ, out var z) || y is < -2048 or > 2048)
            return null;
        var dimension = TeleportDimension.Value ?? "minecraft:overworld";
        return string.Create(culture, $"execute in {dimension} run tp {name} {x} {y} {z}");
    }

    [RelayCommand]
    private void ClosePlayer() => ProfileView = null;

    /// <summary>Acción sobre un jugador: <c>op</c>, <c>deop</c>, <c>kick</c>, <c>ban</c>, <c>pardon</c>, <c>whitelist add</c>...</summary>
    [RelayCommand]
    private void PlayerAction(string? spec)
    {
        // "accion|jugador" desde la lista; "accion" usa el nombre escrito.
        if (string.IsNullOrWhiteSpace(spec))
            return;
        var parts = spec.Split('|', 2);
        var name = (parts.Length == 2 ? parts[1] : PlayerName).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{1,16}$"))
        {
            Error = "Escribe un nombre de jugador válido (hasta 16 letras, números o _).";
            return;
        }

        if (!_runner.SendCommand($"{parts[0]} {name}"))
        {
            Error = "El servidor tiene que estar en marcha para aplicar cambios a los jugadores.";
            return;
        }

        Error = null;
        if (parts.Length == 1)
            PlayerName = string.Empty;
        if (parts[0].StartsWith("whitelist", StringComparison.Ordinal))
            _ = RefreshWhitelistSoonAsync();
    }

    /// <summary>El servidor escribe whitelist.json al momento de aplicar el comando.</summary>
    private async Task RefreshWhitelistSoonAsync()
    {
        await Task.Delay(800);
        RefreshWhitelist();
    }

    // ══════════════════════════════════════════════════════════════════
    // CONFIGURACIÓN (server.properties)
    // ══════════════════════════════════════════════════════════════════

    private string PropertiesPath => Path.Combine(Directory, ServerPropertiesFile.FileName);

    public ServerFilter Difficulty { get; }
    public ServerFilter GameMode { get; }

    [ObservableProperty] private string _motd = string.Empty;
    [ObservableProperty] private string _portText = string.Empty;

    /// <summary><c>server-ip</c>: dirección de este equipo en la que escucha el servidor (vacío = todas).</summary>
    [ObservableProperty] private string _serverIpText = string.Empty;
    [ObservableProperty] private string _maxPlayersText = string.Empty;
    [ObservableProperty] private double _viewDistance = 10;
    [ObservableProperty] private double _simulationDistance = 10;
    [ObservableProperty] private string _seed = string.Empty;
    [ObservableProperty] private bool _onlineMode;
    [ObservableProperty] private bool _pvp;
    [ObservableProperty] private bool _whitelist;
    [ObservableProperty] private bool _allowFlight;
    [ObservableProperty] private bool _commandBlocks;
    [ObservableProperty] private bool _hardcore;
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private double _memoryMb;
    [ObservableProperty] private string? _settingsMessage;

    /// <summary>
    /// PvP y bloques de comandos están en server.properties hasta la 1.21; desde la 26.1 son reglas del juego
    /// (<c>/gamerule</c>). Solo se muestran si el archivo del servidor los tiene.
    /// </summary>
    [ObservableProperty] private bool _hasPvpSetting;
    [ObservableProperty] private bool _hasCommandBlockSetting;

    public double MemoryMinMb => SystemMemory.MinMb;
    public double MemoryMaxMb => SystemMemory.MaxMb;
    public double MemoryStepMb => SystemMemory.StepMb;
    public string MemoryText => SystemMemory.Format((int)MemoryMb);

    partial void OnMemoryMbChanged(double value) => OnPropertyChanged(nameof(MemoryText));

    // Distancias que admite el servidor, en chunks: renderizado (lo que ven los jugadores) 2-32 y simulación
    // (dónde se mueven criaturas, cultivos, redstone…) 5-32. Cuanto más, más CPU y RAM del servidor.
    public const double ViewDistanceMin = 2, ViewDistanceMax = 32, SimulationDistanceMin = 5, SimulationDistanceMax = 32;
    public string ViewDistanceLabel => $"{(int)ViewDistance} chunks";
    public string SimulationDistanceLabel => $"{(int)SimulationDistance} chunks";
    partial void OnViewDistanceChanged(double value) => OnPropertyChanged(nameof(ViewDistanceLabel));
    partial void OnSimulationDistanceChanged(double value) => OnPropertyChanged(nameof(SimulationDistanceLabel));

    /// <summary>El MOTD admite dos líneas (en server.properties se guardan con <c>\n</c>).</summary>
    public const int MotdMaxLines = 2;

    // ── Icono del servidor (server-icon.png, 64×64) ──────────────────────

    [ObservableProperty] private System.Windows.Media.ImageSource? _serverIcon;
    public bool HasServerIcon => ServerIcon is not null;
    partial void OnServerIconChanged(System.Windows.Media.ImageSource? value) => OnPropertyChanged(nameof(HasServerIcon));

    private string IconPath => Path.Combine(Directory, ServerIconFile.FileName);

    private void LoadServerIcon() => ServerIcon = ServerIconFile.Load(IconPath);

    /// <summary>Elige una imagen; se recorta al centro y se escala a 64×64 (lo que exige Minecraft).</summary>
    [RelayCommand]
    private void ChooseServerIcon()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Icono del servidor",
            Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Todos los archivos|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            ServerIconFile.SaveFrom(dialog.FileName, IconPath);
            LoadServerIcon();
            SettingsMessage = IsAlive ? "Icono guardado. Se verá en la lista de servidores al reiniciar el servidor." : "Icono guardado.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            SettingsMessage = "No se pudo usar esa imagen: " + ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveServerIcon()
    {
        try
        {
            File.Delete(IconPath);
            ServerIcon = null;
            SettingsMessage = IsAlive ? "Icono quitado. Se aplica al reiniciar el servidor." : "Icono quitado.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsMessage = $"No se pudo quitar: {ex.Message}";
        }
    }

    /// <summary>Si el mundo aún no existe, la semilla se aplica; después ya no cambia nada.</summary>
    public bool HasWorld => System.IO.Directory.Exists(Path.Combine(Directory, ServerPropertiesFile.Load(PropertiesPath).Get("level-name", "world")));

    private void LoadSettings()
    {
        var props = ServerPropertiesFile.Load(PropertiesPath);
        Motd = props.Get("motd", "A Minecraft Server").Replace("\n", Environment.NewLine);
        PortText = props.Port.ToString();
        ServerIpText = props.Get("server-ip", string.Empty).Trim();
        MaxPlayersText = props.GetInt("max-players", 20).ToString();
        ViewDistance = Math.Clamp(props.GetInt("view-distance", 10), ViewDistanceMin, ViewDistanceMax);
        SimulationDistance = Math.Clamp(props.GetInt("simulation-distance", 10), SimulationDistanceMin, SimulationDistanceMax);
        LoadServerIcon();
        Seed = props.Get("level-seed", string.Empty);
        OnlineMode = props.GetBool("online-mode", true);
        Pvp = props.GetBool("pvp", true);
        Whitelist = props.GetBool("white-list", false);
        AllowFlight = props.GetBool("allow-flight", false);
        CommandBlocks = props.GetBool("enable-command-block", false);
        Hardcore = props.GetBool("hardcore", false);
        HasPvpSetting = props.Get("pvp") is not null;
        HasCommandBlockSetting = props.Get("enable-command-block") is not null;
        Difficulty.SetOptions(Difficulty.Options.Select(o => (o.Value, o.Label)).ToList(), allLabel: null, props.Get("difficulty", "easy"));
        GameMode.SetOptions(GameMode.Options.Select(o => (o.Value, o.Label)).ToList(), allLabel: null, props.Get("gamemode", "survival"));
        MemoryMb = SystemMemory.Clamp(Model.MemoryMb);
        AutoStart = Model.AutoStart;
        OnPropertyChanged(nameof(HasWorld));
    }

    [RelayCommand]
    private void SaveSettings()
    {
        if (!int.TryParse(PortText, out var port) || port is < 1 or > 65535)
        {
            SettingsMessage = "El puerto tiene que ser un número entre 1 y 65535.";
            return;
        }

        var serverIp = ServerIpText.Trim();
        if (serverIp.Length > 0 && !System.Net.IPAddress.TryParse(serverIp, out _))
        {
            SettingsMessage = "La IP tiene que ser una dirección como 192.168.1.20 (o vacía para usar todas).";
            return;
        }

        if (!int.TryParse(MaxPlayersText, out var maxPlayers) || maxPlayers is < 1 or > 1000)
        {
            SettingsMessage = "Jugadores máximos: entre 1 y 1000.";
            return;
        }

        var motdLines = Motd.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (motdLines.Count > 0 && motdLines[^1].Length == 0)
            motdLines.RemoveAt(motdLines.Count - 1);
        if (motdLines.Count > MotdMaxLines)
        {
            SettingsMessage = "El MOTD admite como mucho dos líneas.";
            return;
        }

        if (port == Model.Publish.Port)
        {
            SettingsMessage = "Ese puerto lo usa la publicación del cliente: elige otro.";
            return;
        }

        try
        {
            var props = ServerPropertiesFile.Load(PropertiesPath);
            props.Set("motd", string.Join('\n', motdLines).Trim());
            props.Set("server-port", port);
            props.Set("server-ip", serverIp);
            props.Set("query.port", port);
            props.Set("max-players", maxPlayers);
            props.Set("view-distance", (int)ViewDistance);
            props.Set("simulation-distance", (int)SimulationDistance);
            props.Set("level-seed", Seed.Trim());
            props.Set("online-mode", OnlineMode);
            if (HasPvpSetting)
                props.Set("pvp", Pvp);
            props.Set("white-list", Whitelist);
            props.Set("enforce-whitelist", Whitelist);
            props.Set("allow-flight", AllowFlight);
            if (HasCommandBlockSetting)
                props.Set("enable-command-block", CommandBlocks);
            props.Set("hardcore", Hardcore);
            props.Set("difficulty", Difficulty.Value ?? "easy");
            props.Set("gamemode", GameMode.Value ?? "survival");
            props.Save(PropertiesPath);

            var model = Model.Clone();
            model.MemoryMb = (int)MemoryMb;
            model.AutoStart = AutoStart;
            _hosting.Store.Save(model);
            Model = model;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsMessage = $"No se pudo guardar: {ex.Message}";
            return;
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(LanAddress));
        OnPropertyChanged(nameof(PlayersText));
        _ = RefreshPublicAddressAsync();
        SettingsMessage = IsAlive ? "Guardado. Reinicia el servidor para aplicar los cambios." : "Guardado.";
    }

    // ══════════════════════════════════════════════════════════════════
    // CONTENIDO (mods / plugins) Y CARPETAS
    // ══════════════════════════════════════════════════════════════════

    public ObservableCollection<ContentFile> ContentFiles { get; } = [];
    public bool HasContentFolder => Model.ContentFolder is not null;
    public string ContentTitle => Model.Software == ServerSoftware.Paper ? "Plugins" : "Mods";
    public bool HasNoContent => HasContentFolder && ContentFiles.Count == 0;

    private string? ContentPath => Model.ContentFolder is { } folder ? Path.Combine(Directory, folder) : null;

    [RelayCommand]
    private void RefreshContent()
    {
        ContentFiles.Clear();
        if (ContentPath is { } dir && System.IO.Directory.Exists(dir))
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles()
                         .Where(f => f.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                ContentFiles.Add(new ContentFile(file.Name, file.FullName, FormatSize(file.Length), file.Extension.Equals(".jar", StringComparison.OrdinalIgnoreCase)));
        }

        OnPropertyChanged(nameof(HasNoContent));
    }

    public string AddContentText => Model.Software switch
    {
        ServerSoftware.Paper => "Añadir plugin",
        ServerSoftware.Vanilla => "Explorar contenido",
        _ => "Añadir mod",
    };

    /// <summary>
    /// Para instalar desde Explorar en este servidor: se abre en Mods (en Vanilla, que no carga mods, en Data
    /// packs) y todas las pestañas instalan aquí con su cargador y su versión.
    /// </summary>
    public ServerInstallTarget InstallTarget => new(Id, Name, Model.Clone(), Directory, _hosting.Store.GetClientDirectory(Id),
        Model.ContentFolder is null ? Core.Modrinth.ContentType.DataPack : Core.Modrinth.ContentType.Mod);

    /// <summary>«Añadir mod»: Explorar con el cargador y la versión de este servidor.</summary>
    [RelayCommand]
    private void AddContent() => AddModsRequested?.Invoke(this, InstallTarget);

    [RelayCommand]
    private void ToggleContent(ContentFile? file)
    {
        if (file is null)
            return;
        try
        {
            var target = file.IsEnabled ? file.Path + ".disabled" : file.Path[..^".disabled".Length];
            File.Move(file.Path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"No se pudo cambiar: {ex.Message}";
        }

        RefreshContent();
    }

    [RelayCommand]
    private async Task RemoveContentAsync(ContentFile? file)
    {
        if (file is null)
            return;
        var ok = await _confirm.ShowAsync($"Quitar {file.Name}", "El archivo se borra de la carpeta del servidor.", "Quitar", isDanger: true);
        if (!ok)
            return;
        try
        {
            File.Delete(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"No se pudo borrar: {ex.Message}";
        }

        RefreshContent();
    }

    [RelayCommand]
    private void OpenFolder(string? which)
    {
        var dir = which switch
        {
            "content" when ContentPath is { } c => c,
            "client" => _hosting.Store.GetClientDirectory(Id),
            "world" => Path.Combine(Directory, ServerPropertiesFile.Load(PropertiesPath).Get("level-name", "world")),
            _ => Directory,
        };
        System.IO.Directory.CreateDirectory(dir);
        _dialogs.OpenInExplorer(dir);
    }

    // ══════════════════════════════════════════════════════════════════
    // DIRECCIONES Y PUERTOS
    // ══════════════════════════════════════════════════════════════════

    public async Task RefreshPublicAddressAsync()
    {
        var ip = await _hosting.Network.GetPublicIpAsync();
        _publicIp = ip;
        PublicAddress = ip is null ? null : ip + PortSuffix(Port);
        OnPropertyChanged(nameof(PublishUrl));
    }

    [RelayCommand]
    private async Task CopyAddressAsync(string? which)
    {
        var lan = which == "lan";
        var text = which switch
        {
            "lan" => LanAddress,
            "publish" => PublishUrl,
            _ => PublicAddress,
        };
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return;
        }

        if (which == "publish")
            IsPublishCopied = true;
        else if (lan)
            IsLanCopied = true;
        else
            IsCopied = true;
        await Task.Delay(1400);
        IsCopied = IsLanCopied = IsPublishCopied = false;
    }

    [ObservableProperty] private string? _portMessage;

    /// <summary>
    /// Abre la página del router (su IP, la puerta de enlace). Redirigir los puertos lo hace el jugador: cada
    /// router es distinto y pide su contraseña.
    /// </summary>
    [RelayCommand]
    private void OpenRouter()
    {
        var gateway = NetworkAccess.GetGatewayIp();
        if (gateway is null)
        {
            PortMessage = "No se encontró el router: el equipo no parece estar conectado a una red local.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://{gateway}/") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PortMessage = $"No se pudo abrir el navegador. Entra a http://{gateway}/";
            return;
        }

        var ports = Model.Publish.Enabled ? $"los puertos TCP {Port} y {Model.Publish.Port}" : $"el puerto TCP {Port}";
        PortMessage = $"Se abrió la página del router (http://{gateway}/). Entra con su usuario y contraseña y redirige {ports} a este equipo ({NetworkAccess.GetLanIp() ?? "esta PC"}).";
    }

    public string RouterTip => $"Abre la página del router ({NetworkAccess.GetGatewayIp() ?? "no encontrado"}) para redirigir los puertos a este equipo";

    // ══════════════════════════════════════════════════════════════════
    // PUBLICAR CLIENTE (sincronización de launchers)
    // ══════════════════════════════════════════════════════════════════

    [ObservableProperty] private bool _publishEnabled;
    [ObservableProperty] private string _publishPortText = string.Empty;
    [ObservableProperty] private string _clientVersion = string.Empty;
    [ObservableProperty] private bool _includeServerMods;
    [ObservableProperty] private bool _deleteUnauthorized;
    [ObservableProperty] private bool _alwaysOnline;
    [ObservableProperty] private bool _lockServerUrl;
    [ObservableProperty] private bool _protectPacks;
    [ObservableProperty] private bool _autoJoin;
    [ObservableProperty] private string _publicHost = string.Empty;
    [ObservableProperty] private bool _recommendMemory;
    [ObservableProperty] private bool _verifyHashes;
    [ObservableProperty] private double _recommendedMemoryMb = DefaultRecommendedMemoryMb;
    [ObservableProperty] private string? _publishMessage;

    /// <summary>RAM que se propone al activar la opción: la del launcher (4 GB) suele quedarse corta con muchos mods.</summary>
    public const int DefaultRecommendedMemoryMb = 6144;
    public const double RecommendedMemoryMinMb = 2048;
    public const double RecommendedMemoryMaxMb = 16384;
    public double RecommendedMemoryStepMb => Core.Configuration.SystemMemory.StepMb;
    public string RecommendedMemoryText => Core.Configuration.SystemMemory.Format((int)RecommendedMemoryMb);

    partial void OnRecommendedMemoryMbChanged(double value) => OnPropertyChanged(nameof(RecommendedMemoryText));

    /// <summary>Mods del servidor que no se envían porque Modrinth los marca como solo de servidor.</summary>
    public string? SkippedModsText => _publisher.Current?.SkippedServerMods is { Count: > 0 } skipped
        ? $"No se envían {skipped.Count} {(skipped.Count == 1 ? "mod" : "mods")} solo de servidor: {string.Join(", ", skipped.Take(4))}{(skipped.Count > 4 ? "…" : string.Empty)}"
        : null;
    [ObservableProperty] private bool _isPublishCopied;

    public bool IsPublishing => _publisher.IsRunning;
    public bool IsModded => Model.IsModded;
    public string? PublishError => _publisher.LastError;

    /// <summary>Dirección que se escribe en el launcher de cada jugador (con la IP pública si se conoce).</summary>
    public string PublishUrl => $"http://{_publicIp ?? NetworkAccess.GetLanIp() ?? "localhost"}:{Model.Publish.Port}";

    private string? _publicIp;

    public string PublishStatusText
    {
        get
        {
            if (!_publisher.IsRunning)
                return Model.Publish.Enabled ? "Se activa al iniciar el servidor." : "Desactivada.";
            if (_publisher.IsBuilding)
                return "Generando el manifest…";
            var current = _publisher.Current;
            var activity = _publisher.Activity;
            return current is null
                ? "Publicando…"
                : $"{current.FileCount} archivos ({FormatSize(current.TotalBytes)}) · {activity.Verifications} verificaciones · {activity.FilesServed} archivos servidos · {activity.DistinctClients} equipos";
        }
    }

    /// <summary>El formulario con lo guardado y el estado. Sus cambios no cuentan como ediciones (no se autoguardan).</summary>
    private void RefreshPublish()
    {
        _refreshingPublish = true;
        try
        {
            RefreshPublishForm();
        }
        finally
        {
            _refreshingPublish = false;
        }

        RefreshPublishStatus();
    }

    private void RefreshPublishForm()
    {
        var p = Model.Publish;
        PublishEnabled = p.Enabled;
        PublishPortText = p.Port.ToString();
        ClientVersion = p.ClientVersion;
        IncludeServerMods = p.IncludeServerMods;
        DeleteUnauthorized = p.DeleteUnauthorized;
        AlwaysOnline = p.AlwaysOnline;
        LockServerUrl = p.LockServerUrl;
        ProtectPacks = p.ProtectPacks;
        AutoJoin = p.AutoJoin;
        PublicHost = p.PublicHost;
        RecommendMemory = p.RecommendedMemoryMb is not null;
        VerifyHashes = p.VerifyHashes;
        RecommendedMemoryMb = Math.Clamp(p.RecommendedMemoryMb ?? DefaultRecommendedMemoryMb, RecommendedMemoryMinMb, RecommendedMemoryMaxMb);
    }

    private void RefreshPublishStatus()
    {
        OnPropertyChanged(nameof(IsPublishing));
        OnPropertyChanged(nameof(SkippedModsText));
        OnPropertyChanged(nameof(PublishStatusText));
        OnPropertyChanged(nameof(PublishError));
        OnPropertyChanged(nameof(PublishUrl));
    }

    // ── Guardado automático ──────────────────────────────────────────────
    // Cada cambio del formulario se guarda solo tras una breve pausa (al escribir o arrastrar no se guarda
    // a medias). El botón Guardar sigue funcionando igual.

    private static readonly HashSet<string> PublishFields =
    [
        nameof(PublishEnabled), nameof(PublishPortText), nameof(ClientVersion), nameof(IncludeServerMods), nameof(DeleteUnauthorized),
        nameof(AlwaysOnline), nameof(LockServerUrl), nameof(ProtectPacks), nameof(AutoJoin), nameof(PublicHost), nameof(RecommendMemory),
        nameof(RecommendedMemoryMb), nameof(VerifyHashes),
    ];

    public static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(800);

    private bool _refreshingPublish;
    private bool _savingPublish;
    private CancellationTokenSource? _autoSave;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_refreshingPublish && e.PropertyName is { } name && PublishFields.Contains(name))
            ScheduleAutoSave();
    }

    private async void ScheduleAutoSave()
    {
        _autoSave?.Cancel();
        var cts = _autoSave = new CancellationTokenSource();
        try
        {
            await Task.Delay(AutoSaveDelay, cts.Token);
            while (_savingPublish) // un guardado en curso: se espera a que termine
                await Task.Delay(100, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (PublishFormDiffers())
            await SavePublishCoreAsync(automatic: true);
    }

    /// <summary>El formulario tiene algo distinto de lo guardado.</summary>
    private bool PublishFormDiffers()
    {
        var p = Model.Publish;
        return PublishEnabled != p.Enabled || PublishPortText.Trim() != p.Port.ToString() || ClientVersion.Trim() != p.ClientVersion
               || IncludeServerMods != p.IncludeServerMods || DeleteUnauthorized != p.DeleteUnauthorized || AlwaysOnline != p.AlwaysOnline
               || LockServerUrl != p.LockServerUrl || ProtectPacks != p.ProtectPacks || AutoJoin != p.AutoJoin
               || PublicHost.Trim() != p.PublicHost || (RecommendMemory ? (int?)RecommendedMemoryMb : null) != p.RecommendedMemoryMb
               || VerifyHashes != p.VerifyHashes;
    }

    /// <summary>Muestra el aviso de guardado y lo quita a los pocos segundos (si no lo sustituyó otro).</summary>
    private void FlashSaved(string text)
    {
        PublishMessage = null; // un error anterior ya no aplica
        FlashNotice(text, isError: false);
    }

    private async void FlashNotice(string text, bool isError)
    {
        var id = ++_savedNoticeId;
        IsNoticeError = isError;
        SavedNotice = text;
        await Task.Delay(isError ? 4500 : 2500);
        if (id == _savedNoticeId)
            SavedNotice = null;
    }

    private int _savedNoticeId;

    /// <summary>Aviso flotante «guardado» (se quita solo).</summary>
    [ObservableProperty] private string? _savedNotice;
    [ObservableProperty] private bool _isNoticeError;

    [RelayCommand]
    private Task SavePublishAsync() => SavePublishCoreAsync(automatic: false);

    private async Task SavePublishCoreAsync(bool automatic)
    {
        _autoSave?.Cancel();
        _savingPublish = true;
        try
        {
            // Lo que no se pudo guardar se avisa también en el aviso flotante (el mensaje de abajo puede no verse).
            if (!await SavePublishInnerAsync(automatic) && automatic && PublishMessage is { } error)
                FlashNotice(error, isError: true);
        }
        finally
        {
            _savingPublish = false;
        }
    }

    private async Task<bool> SavePublishInnerAsync(bool automatic)
    {
        if (!int.TryParse(PublishPortText, out var port) || port is < 1 or > 65535)
        {
            PublishMessage = "El puerto tiene que ser un número entre 1 y 65535.";
            return false;
        }

        if (port == Port)
        {
            PublishMessage = "Ese puerto lo usa el servidor de Minecraft: elige otro.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ClientVersion) || ClientVersion.Trim().Length > 32)
        {
            PublishMessage = "Escribe la versión del cliente (por ejemplo 1.0.0).";
            return false;
        }

        var model = Model.Clone();
        var portChanged = model.Publish.Port != port;
        model.Publish.Enabled = PublishEnabled;
        model.Publish.Port = port;
        model.Publish.ClientVersion = ClientVersion.Trim();
        model.Publish.IncludeServerMods = IncludeServerMods;
        model.Publish.DeleteUnauthorized = DeleteUnauthorized;
        model.Publish.AlwaysOnline = AlwaysOnline;
        model.Publish.LockServerUrl = LockServerUrl;
        model.Publish.ProtectPacks = ProtectPacks;
        model.Publish.AutoJoin = AutoJoin;
        model.Publish.RecommendedMemoryMb = RecommendMemory ? (int)RecommendedMemoryMb : null;
        model.Publish.VerifyHashes = VerifyHashes;
        var host = PublicHost.Trim();
        if (host.Length > 0 && !Core.Servers.ServerAddress.TryParse(host, out _) || host.Contains(':'))
        {
            PublishMessage = "Dirección pública: solo el dominio o la IP, sin puerto (por ejemplo mi-servidor.com).";
            return false;
        }

        model.Publish.PublicHost = host;
        try
        {
            _hosting.Store.Save(model);
        }
        catch (IOException ex)
        {
            PublishMessage = $"No se pudo guardar: {ex.Message}";
            return false;
        }

        Model = model;
        var saved = automatic ? "Cambios guardados automáticamente." : "Guardado.";
        // Paquete de recursos obligatorio en server.properties (y dirección para entrar directamente).
        var packMessage = await _hosting.PrepareSyncAsync(model);
        if (packMessage is not null)
            PublishMessage = $"{saved} {packMessage}{(IsAlive && model.Publish.ProtectPacks ? " Reinicia el servidor para aplicarlo." : string.Empty)}";
        else
            FlashSaved(saved);
        try
        {
            // Activar con el servidor en marcha publica ya; desactivar deja de publicar.
            if (!model.Publish.Enabled && _publisher.IsRunning)
                await _publisher.StopAsync();
            else if (model.Publish.Enabled && (IsAlive || _publisher.IsRunning))
            {
                if (portChanged || !_publisher.IsRunning)
                    await _hosting.StartPublishingAsync(model);
                else
                    await _publisher.UpdateSettingsAsync(model);
            }
        }
        catch (InvalidOperationException ex)
        {
            PublishMessage = ex.Message;
        }

        // Automático: no se recarga el formulario (se perdería lo que se esté escribiendo mientras tanto).
        if (automatic)
            RefreshPublishStatus();
        else
            RefreshPublish();
        return true;
    }

    /// <summary>Publicar ahora sin iniciar el juego (p. ej. para que los jugadores descarguen antes).</summary>
    [RelayCommand]
    private async Task TogglePublishingAsync()
    {
        try
        {
            if (_publisher.IsRunning)
                await _publisher.StopAsync();
            else
                await _hosting.StartPublishingAsync(Model);
            PublishMessage = null;
        }
        catch (InvalidOperationException ex)
        {
            PublishMessage = ex.Message;
        }

        RefreshPublish();
    }

    [RelayCommand]
    private async Task RegenerateAsync()
    {
        if (_publisher.IsRunning)
            await _publisher.RegenerateAsync();
    }

    // ══════════════════════════════════════════════════════════════════
    // UTILIDADES
    // ══════════════════════════════════════════════════════════════════

    private static string PortSuffix(int port) => port == ServerPropertiesFile.DefaultPort ? string.Empty : $":{port}";

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    private static string FormatSpan(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{span.Minutes} min" : $"{span.Seconds} s";
}
