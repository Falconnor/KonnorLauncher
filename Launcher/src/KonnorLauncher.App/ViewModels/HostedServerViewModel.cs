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

    /// <summary>Dirección para los jugadores de la misma red (siempre se conoce).</summary>
    public string LanAddress => $"{NetworkAccess.GetLanIp() ?? "localhost"}{PortSuffix(Port)}";

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
        Players.Clear();
        foreach (var name in known.Select(k => k.Name).Union(online, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(online.Contains)
                     .ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var profile = uuids.TryGetValue(name, out var uuid) ? ReadProfile(name, uuid) : null;
            Players.Add(new PlayerEntry(name, online.Contains(name), profile));
        }

        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(PlayersText));
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

    // ── Perfil de un jugador ─────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayerProfileOpen))]
    private PlayerProfileViewModel? _profileView;

    [ObservableProperty] private bool _isLoadingProfile;

    public bool IsPlayerProfileOpen => ProfileView is not null;

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
        if (!entry.IsOnline || !IsRunning)
            return;

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
                ProfileView = new PlayerProfileViewModel(fresh, entry.IsOnline);
        }
        finally
        {
            IsLoadingProfile = false;
        }
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
    }

    // ══════════════════════════════════════════════════════════════════
    // CONFIGURACIÓN (server.properties)
    // ══════════════════════════════════════════════════════════════════

    private string PropertiesPath => Path.Combine(Directory, ServerPropertiesFile.FileName);

    public ServerFilter Difficulty { get; }
    public ServerFilter GameMode { get; }

    [ObservableProperty] private string _motd = string.Empty;
    [ObservableProperty] private string _portText = string.Empty;
    [ObservableProperty] private string _maxPlayersText = string.Empty;
    [ObservableProperty] private string _viewDistanceText = string.Empty;
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

    /// <summary>Si el mundo aún no existe, la semilla se aplica; después ya no cambia nada.</summary>
    public bool HasWorld => System.IO.Directory.Exists(Path.Combine(Directory, ServerPropertiesFile.Load(PropertiesPath).Get("level-name", "world")));

    private void LoadSettings()
    {
        var props = ServerPropertiesFile.Load(PropertiesPath);
        Motd = props.Get("motd", "A Minecraft Server");
        PortText = props.Port.ToString();
        MaxPlayersText = props.GetInt("max-players", 20).ToString();
        ViewDistanceText = props.GetInt("view-distance", 10).ToString();
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

        if (!int.TryParse(MaxPlayersText, out var maxPlayers) || maxPlayers is < 1 or > 1000)
        {
            SettingsMessage = "Jugadores máximos: entre 1 y 1000.";
            return;
        }

        if (!int.TryParse(ViewDistanceText, out var viewDistance) || viewDistance is < 2 or > 32)
        {
            SettingsMessage = "Distancia de renderizado: entre 2 y 32 chunks.";
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
            props.Set("motd", Motd.Trim());
            props.Set("server-port", port);
            props.Set("query.port", port);
            props.Set("max-players", maxPlayers);
            props.Set("view-distance", viewDistance);
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

    public string ContentFolderText => Model.Software == ServerSoftware.Paper ? "Carpeta de plugins" : "Carpeta de mods";
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

    /// <summary>Copia mods o plugins (.jar) elegidos por el jugador a la carpeta del servidor.</summary>
    [RelayCommand]
    private void AddContentFromFile()
    {
        if (ContentPath is not { } dir)
            return;
        var file = _dialogs.PickFile($"Añadir {ContentTitle.ToLowerInvariant()}", "Archivos .jar|*.jar");
        if (file is null)
            return;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"No se pudo copiar: {ex.Message}";
        }

        RefreshContent();
    }

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
