using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Hosting.Exaroton;
using KonnorLauncher.Core.Servers;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

/// <summary>Proveedor que se puede elegir al añadir un hosting de terceros.</summary>
public sealed partial class ThirdPartyOption(ThirdPartyProvider provider, string description) : ObservableObject
{
    public ThirdPartyProvider Provider { get; } = provider;
    public string Name { get; } = ThirdPartyEntry.ProviderName(provider);
    public string Description { get; } = description;
    public bool UsesApi => Provider == ThirdPartyProvider.Exaroton;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Un servidor de un hosting de terceros en la lista: uno de exaroton (de una cuenta guardada) o un panel web. Del de
/// exaroton se sabe todo por su API; del panel web, si tiene dirección de juego, su estado por mcstatus.io.
/// </summary>
public sealed partial class ThirdPartyServerItem : ObservableObject
{
    public ThirdPartyServerItem(ThirdPartyEntry entry, ExarotonClient? client = null, ExarotonServer? server = null)
    {
        Entry = entry;
        Client = client;
        Name = entry.Name;
        Address = entry.JoinAddress ?? string.Empty;
        StatusText = IsExaroton ? string.Empty : string.IsNullOrWhiteSpace(entry.JoinAddress) ? "Panel web" : "Comprobando…";
        if (server is not null)
            Apply(server);
    }

    public ThirdPartyEntry Entry { get; }
    public ExarotonClient? Client { get; }
    public string? ExarotonId { get; private set; }
    public bool IsExaroton => Entry.Provider == ThirdPartyProvider.Exaroton;
    public bool IsPanel => !IsExaroton;
    public string ProviderName => ThirdPartyEntry.ProviderName(Entry.Provider);
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _software = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _playersText = string.Empty;
    [ObservableProperty] private bool _isOnline;

    /// <summary>Icono del hosting (el de su web); sin él, la inicial del nombre.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private System.Windows.Media.ImageSource? _icon;

    public bool HasIcon => Icon is not null;

    /// <summary>exaroton: encendido o en camino (se puede detener; si no, iniciar).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStop))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStop))]
    private bool _isBusy;

    public bool CanStart => IsExaroton && !IsRunning && !IsBusy;
    public bool CanStop => IsExaroton && IsRunning && !IsBusy;

    /// <summary>Jugadores conectados (exaroton los lista si el servidor lo permite).</summary>
    public ObservableCollection<string> Players { get; } = [];

    public void Apply(ExarotonServer server)
    {
        ExarotonId = server.Id;
        Name = server.Name;
        Address = server.Address;
        Software = server.SoftwareText;
        StatusText = server.StatusText;
        IsOnline = server.IsOnline;
        IsRunning = server.IsRunning;
        PlayersText = server.Players is { } p && server.IsOnline ? $"{p.Count} / {p.Max} jugadores" : string.Empty;
        Players.Clear();
        foreach (var player in server.Players?.List ?? [])
            Players.Add(player);
    }

    public void Apply(ServerStatus? status)
    {
        IsOnline = status?.Online == true;
        StatusText = status is null ? "Sin respuesta" : status.Online ? "En línea" : "Apagado";
        PlayersText = status is { Online: true } s ? $"{s.PlayersOnline} / {s.PlayersMax} jugadores" : string.Empty;
        Software = status?.Version ?? string.Empty;
    }
}

/// <summary>
/// Hosting → Terceros: servidores alojados en otros sitios. exaroton, con su API oficial: estado, encender/apagar/
/// reiniciar, consola y comandos. Aternos, Hostinger, Modrinth Servers y cualquier otro: su panel web dentro del launcher
/// (la sesión se recuerda) y, si se indica la dirección, su estado y jugadores. Los tokens se guardan cifrados (DPAPI).
/// </summary>
public sealed partial class ThirdPartyViewModel : ObservableObject
{
    private readonly ThirdPartyStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ServerStatusClient _status;
    private readonly ConfirmViewModel _confirm;
    private readonly ILogger<ThirdPartyViewModel> _logger;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _loaded;
    private bool _polling;

    public ThirdPartyViewModel(ThirdPartyStore store, IHttpClientFactory httpFactory, ServerStatusClient status, ConfirmViewModel confirm,
        ILogger<ThirdPartyViewModel> logger)
    {
        _store = store;
        _httpFactory = httpFactory;
        _status = status;
        _confirm = confirm;
        _logger = logger;
        _poll.Tick += async (_, _) => await PollAsync();
        Options =
        [
            new(ThirdPartyProvider.Exaroton, "Control completo desde aquí: encender, apagar, consola y jugadores."),
            new(ThirdPartyProvider.Aternos, "Su panel, dentro del launcher."),
            new(ThirdPartyProvider.Hostinger, "Su panel (hPanel), dentro del launcher."),
            new(ThirdPartyProvider.Modrinth, "Su panel de servidores, dentro del launcher."),
            new(ThirdPartyProvider.Other, "El panel web de cualquier otro hosting."),
        ];
        SelectOption(Options[0]);
    }

    public ObservableCollection<ThirdPartyServerItem> Items { get; } = [];
    public IReadOnlyList<ThirdPartyOption> Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoItems))]
    private bool _isLoading;

    public bool HasNoItems => !IsLoading && Items.Count == 0;

    [ObservableProperty] private string? _listError;

    // ── Vistas: lista, añadir o un servidor abierto ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList), nameof(IsDetail), nameof(IsExarotonDetail), nameof(IsPanelDetail), nameof(PanelUri))]
    private ThirdPartyServerItem? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList))]
    private bool _isAdding;

    public bool IsList => Selected is null && !IsAdding;
    public bool IsDetail => Selected is not null;
    public bool IsExarotonDetail => Selected?.IsExaroton == true;
    public bool IsPanelDetail => Selected?.IsPanel == true;

    /// <summary>Panel web que se abre en el navegador integrado (solo http/https).</summary>
    public Uri? PanelUri => Selected is { IsPanel: true } item && ThirdPartyEntry.IsValidPanel(item.Entry.PanelUrl, out var uri) ? uri : null;

    // ── Formulario «Añadir» ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsToken), nameof(NeedsPanel))]
    private ThirdPartyOption? _option;

    public bool NeedsToken => Option?.UsesApi == true;
    public bool NeedsPanel => Option?.UsesApi == false;

    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newPanelUrl = string.Empty;
    [ObservableProperty] private string _newJoinAddress = string.Empty;
    [ObservableProperty] private string _newToken = string.Empty;
    [ObservableProperty] private string? _addError;
    [ObservableProperty] private bool _isSaving;

    // ── exaroton abierto: consola ──

    [ObservableProperty] private string _consoleText = string.Empty;
    [ObservableProperty] private string _commandText = string.Empty;
    [ObservableProperty] private string? _detailError;

    /// <summary>Cambió el texto de la consola (la vista baja al final).</summary>
    public event EventHandler? ConsoleUpdated;

    /// <summary>Al abrir la pestaña Terceros: la lista (la primera vez, o con «Actualizar»).</summary>
    public async Task ShowAsync()
    {
        if (_loaded)
            return;
        _loaded = true;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ListError = null;
        Items.Clear();
        var errors = new List<string>();
        foreach (var entry in _store.LoadAll())
        {
            if (entry.Provider != ThirdPartyProvider.Exaroton)
            {
                Items.Add(new ThirdPartyServerItem(entry));
                continue;
            }

            if (TokenProtector.Unprotect(entry.ProtectedToken) is not { } token)
            {
                errors.Add($"No se pudo leer el token de la cuenta de exaroton «{entry.Name}»: vuelve a añadirla.");
                continue;
            }

            var client = new ExarotonClient(_httpFactory, token);
            try
            {
                foreach (var server in await client.GetServersAsync())
                    Items.Add(new ThirdPartyServerItem(entry, client, server));
            }
            catch (Exception ex) when (ex is ExarotonException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(ex, "No se pudieron leer los servidores de exaroton de {Account}", entry.Name);
                errors.Add($"exaroton «{entry.Name}»: {(ex is ExarotonException ? ex.Message : "sin conexión")}");
            }
        }

        IsLoading = false;
        OnPropertyChanged(nameof(HasNoItems));
        // El icono de cada hosting (de su web; en caché después de la primera vez).
        foreach (var item in Items)
            _ = LoadIconAsync(item);
        ListError = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null;
        // El estado de los paneles web con dirección, a la vez.
        await Task.WhenAll(Items.Where(i => i.IsPanel && !string.IsNullOrWhiteSpace(i.Entry.JoinAddress)).Select(UpdatePanelStatusAsync));
    }

    private async Task LoadIconAsync(ThirdPartyServerItem item) => item.Icon = await SiteIcons.GetAsync(_httpFactory, item.Entry);

    private async Task UpdatePanelStatusAsync(ThirdPartyServerItem item)
    {
        if (!ServerAddress.TryParse(item.Entry.JoinAddress, out var address))
        {
            item.StatusText = "Dirección no válida";
            return;
        }

        try
        {
            item.Apply(await _status.GetAsync(address));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            item.Apply((ServerStatus?)null);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // AÑADIR
    // ══════════════════════════════════════════════════════════════════

    [RelayCommand]
    private void StartAdd()
    {
        AddError = null;
        NewName = NewPanelUrl = NewJoinAddress = NewToken = string.Empty;
        SelectOption(Options[0]);
        Selected = null;
        IsAdding = true;
    }

    [RelayCommand]
    private void CancelAdd() => IsAdding = false;

    [RelayCommand]
    private void SelectOption(ThirdPartyOption? option)
    {
        if (option is null)
            return;
        foreach (var o in Options)
            o.IsSelected = o == option;
        Option = option;
        NewPanelUrl = ThirdPartyEntry.DefaultPanel(option.Provider) ?? string.Empty;
        AddError = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Option is not { } option)
            return;
        AddError = null;
        if (option.UsesApi)
        {
            await AddExarotonAsync();
            return;
        }

        if (!ThirdPartyEntry.IsValidPanel(NewPanelUrl, out var panel))
        {
            AddError = "Escribe la dirección del panel (empieza por https://).";
            return;
        }

        if (NewJoinAddress.Trim().Length > 0 && !ServerAddress.TryParse(NewJoinAddress, out _))
        {
            AddError = "La dirección del servidor no es válida (por ejemplo: miservidor.aternos.me).";
            return;
        }

        var name = NewName.Trim().Length > 0 ? NewName.Trim() : NewJoinAddress.Trim().Length > 0 ? NewJoinAddress.Trim() : option.Name;
        _store.Add(new ThirdPartyEntry
        {
            Provider = option.Provider,
            Name = name,
            PanelUrl = panel.AbsoluteUri,
            JoinAddress = NewJoinAddress.Trim().Length > 0 ? NewJoinAddress.Trim() : null,
        });
        IsAdding = false;
        await RefreshAsync();
    }

    /// <summary>exaroton: se comprueba el token (su cuenta) antes de guardarlo, cifrado.</summary>
    private async Task AddExarotonAsync()
    {
        var token = NewToken.Trim();
        if (token.Length == 0)
        {
            AddError = "Pega el token de tu cuenta de exaroton.";
            return;
        }

        IsSaving = true;
        try
        {
            var account = await new ExarotonClient(_httpFactory, token).GetAccountAsync();
            _store.Add(new ThirdPartyEntry
            {
                Provider = ThirdPartyProvider.Exaroton,
                Name = string.IsNullOrWhiteSpace(account.Name) ? "exaroton" : account.Name,
                ProtectedToken = TokenProtector.Protect(token),
            });
            NewToken = string.Empty;
            IsAdding = false;
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is ExarotonException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AddError = ex is ExarotonException ? ex.Message : "No se pudo conectar con exaroton: revisa la conexión.";
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Abre la página de exaroton donde se crean los tokens.</summary>
    [RelayCommand]
    private static void OpenExarotonTokens() => OpenExternal("https://exaroton.com/account/");

    // ══════════════════════════════════════════════════════════════════
    // ABRIR, CONTROLAR, QUITAR
    // ══════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task OpenAsync(ThirdPartyServerItem? item)
    {
        if (item is null)
            return;
        IsAdding = false;
        DetailError = null;
        ConsoleText = string.Empty;
        CommandText = string.Empty;
        Selected = item;
        if (item.IsExaroton)
        {
            await PollAsync();
            _poll.Start();
        }
    }

    [RelayCommand]
    private void Back()
    {
        _poll.Stop();
        Selected = null;
    }

    /// <summary>Al salir de la pestaña o de la sección: deja de consultar exaroton.</summary>
    public void Pause() => _poll.Stop();

    /// <summary>Al volver: si hay un servidor de exaroton abierto, se vuelve a consultar.</summary>
    public void Resume()
    {
        if (Selected is { IsExaroton: true })
            _poll.Start();
    }

    /// <summary>exaroton abierto: su estado y su consola cada pocos segundos.</summary>
    private async Task PollAsync()
    {
        if (_polling || Selected is not { IsExaroton: true, Client: { } client, ExarotonId: { } id } item)
            return;
        _polling = true;
        try
        {
            item.Apply(await client.GetServerAsync(id));
            var log = item.IsRunning ? await client.GetLogsAsync(id) : ConsoleText;
            if (log != ConsoleText)
            {
                ConsoleText = log;
                ConsoleUpdated?.Invoke(this, EventArgs.Empty);
            }

            DetailError = null;
        }
        catch (Exception ex) when (ex is ExarotonException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            DetailError = ex is ExarotonException ? ex.Message : "Sin conexión con exaroton.";
        }
        finally
        {
            _polling = false;
        }
    }

    [RelayCommand]
    private Task StartServerAsync(ThirdPartyServerItem? item) => PowerAsync(item, (c, id) => c.StartAsync(id), "Iniciando…");

    [RelayCommand]
    private Task StopServerAsync(ThirdPartyServerItem? item) => PowerAsync(item, (c, id) => c.StopAsync(id), "Deteniendo…");

    [RelayCommand]
    private Task RestartServerAsync(ThirdPartyServerItem? item) => PowerAsync(item, (c, id) => c.RestartAsync(id), "Reiniciando…");

    private async Task PowerAsync(ThirdPartyServerItem? item, Func<ExarotonClient, string, Task> action, string busyText)
    {
        item ??= Selected;
        if (item is not { Client: { } client, ExarotonId: { } id })
            return;
        item.IsBusy = true;
        item.StatusText = busyText;
        try
        {
            await action(client, id);
            await Task.Delay(800);
            item.Apply(await client.GetServerAsync(id));
            DetailError = null;
        }
        catch (Exception ex) when (ex is ExarotonException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            var message = ex is ExarotonException ? ex.Message : "Sin conexión con exaroton.";
            if (Selected == item)
                DetailError = message;
            else
                ListError = $"{item.Name}: {message}";
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendCommandAsync()
    {
        var command = CommandText.Trim().TrimStart('/');
        if (command.Length == 0 || Selected is not { Client: { } client, ExarotonId: { } id })
            return;
        try
        {
            await client.CommandAsync(id, command);
            CommandText = string.Empty;
            DetailError = null;
            await Task.Delay(500);
            await PollAsync();
        }
        catch (Exception ex) when (ex is ExarotonException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            DetailError = ex is ExarotonException ? ex.Message : "Sin conexión con exaroton.";
        }
    }

    /// <summary>Quita el panel web, o la cuenta de exaroton (con todos sus servidores) del launcher. En el hosting no se borra nada.</summary>
    [RelayCommand]
    private async Task RemoveAsync(ThirdPartyServerItem? item)
    {
        if (item is null)
            return;
        var account = item.IsExaroton;
        var title = account ? $"Quitar la cuenta de exaroton «{item.Entry.Name}»" : $"Quitar «{item.Name}»";
        var message = account
            ? "Sus servidores dejan de verse aquí. En exaroton no se borra nada."
            : "Deja de verse aquí. En el hosting no se borra nada.";
        if (!await _confirm.ShowAsync(title, message, "Quitar", isDanger: true))
            return;
        _store.Remove(item.Entry.Id);
        if (Selected?.Entry.Id == item.Entry.Id)
            Back();
        await RefreshAsync();
    }

    /// <summary>Abre el panel (o la página de exaroton) en el navegador del sistema.</summary>
    [RelayCommand]
    private void OpenInBrowser(ThirdPartyServerItem? item)
    {
        item ??= Selected;
        if (item is null)
            return;
        OpenExternal(item.IsExaroton ? "https://exaroton.com/servers/" : item.Entry.PanelUrl);
    }

    private static void OpenExternal(string? url)
    {
        if (!ThirdPartyEntry.IsValidPanel(url, out var uri))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
