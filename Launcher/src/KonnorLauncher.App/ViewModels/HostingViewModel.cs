using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KonnorLauncher.App.Services;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Loaders;
using Microsoft.Extensions.Logging;

namespace KonnorLauncher.App.ViewModels;

public enum HostingTab
{
    Servers,
    Create,
}

/// <summary>Software que se puede elegir al crear un servidor.</summary>
public sealed partial class SoftwareOption(ServerSoftware software, string description) : ObservableObject
{
    public ServerSoftware Software { get; } = software;
    public string Name { get; } = HostedServer.SoftwareName(software);
    public string Description { get; } = description;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Sección Hosting: crear y gestionar servidores de Minecraft en este equipo (lo que hacía ServerManager)
/// y publicar su cliente para los launchers de los jugadores (lo que hacía LauncherBackEnd).
/// </summary>
public sealed partial class HostingViewModel : ObservableObject
{
    private readonly HostingService _hosting;
    private readonly ServerSoftwareCatalog _catalog;
    private readonly ConfirmViewModel _confirm;
    private readonly IDialogService _dialogs;
    private readonly ILogger<HostingViewModel> _logger;
    private bool _loaded;
    private int _versionRequest;
    private int _loaderRequest;

    public HostingViewModel(HostingService hosting, ServerSoftwareCatalog catalog, ConfirmViewModel confirm, IDialogService dialogs,
        ILogger<HostingViewModel> logger)
    {
        _hosting = hosting;
        _catalog = catalog;
        _confirm = confirm;
        _dialogs = dialogs;
        _logger = logger;

        SoftwareOptions =
        [
            new(ServerSoftware.Vanilla, "El servidor oficial de Mojang, sin mods."),
            new(ServerSoftware.Paper, "Rápido y con plugins (Bukkit/Spigot). Los jugadores entran sin mods."),
            new(ServerSoftware.Fabric, "Mods ligeros y actualizados enseguida a cada versión."),
            new(ServerSoftware.Forge, "Los modpacks clásicos y los mods más grandes."),
            new(ServerSoftware.NeoForge, "La continuación de Forge para las versiones recientes."),
            new(ServerSoftware.Quilt, "Compatible con la mayoría de mods de Fabric."),
        ];
        MinecraftVersion = new ServerFilter("Versión", OnMinecraftVersionChanged);
        LoaderVersion = new ServerFilter("Cargador", () => OnPropertyChanged(nameof(CanCreate)));
        SelectSoftware(SoftwareOptions[0]);
    }

    public ObservableCollection<HostedServerViewModel> Servers { get; } = [];
    public bool HasNoServers => Servers.Count == 0;

    // ══════════════════════════════════════════════════════════════════
    // NAVEGACIÓN
    // ══════════════════════════════════════════════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServersTab), nameof(IsCreateTab), nameof(IsList), nameof(IsCreate))]
    private HostingTab _tab = HostingTab.Servers;

    /// <summary>Servidor abierto (su página ocupa la sección).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetail), nameof(IsList), nameof(IsCreate))]
    private HostedServerViewModel? _selected;

    public bool IsServersTab { get => Tab == HostingTab.Servers; set { if (value) { Tab = HostingTab.Servers; Selected = null; } } }
    public bool IsCreateTab { get => Tab == HostingTab.Create; set { if (value) { Tab = HostingTab.Create; Selected = null; } } }
    public bool IsDetail => Selected is not null;
    public bool IsList => Selected is null && Tab == HostingTab.Servers;
    public bool IsCreate => Selected is null && Tab == HostingTab.Create;

    /// <summary>Al entrar en la sección: lee los servidores y prepara el formulario.</summary>
    public async Task OpenAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            Reload();
            await LoadMinecraftVersionsAsync();
        }

        foreach (var server in Servers)
            _ = server.RefreshPublicAddressAsync();
    }

    /// <summary>Esc vuelve de la página de un servidor a la lista.</summary>
    public bool CloseTopmost()
    {
        foreach (var filter in new[] { MinecraftVersion, LoaderVersion })
        {
            if (filter.IsOpen)
            {
                filter.IsOpen = false;
                return true;
            }
        }

        if (Selected is not null)
        {
            Selected = null;
            return true;
        }

        return false;
    }

    [RelayCommand]
    private void Open(HostedServerViewModel? server)
    {
        if (server is null)
            return;
        Selected = server;
        _ = server.RefreshPublicAddressAsync();
    }

    [RelayCommand]
    private void Back() => Selected = null;

    private void Reload()
    {
        Servers.Clear();
        foreach (var model in _hosting.Store.LoadAll())
            Servers.Add(CreateViewModel(model));
        OnPropertyChanged(nameof(HasNoServers));
    }

    private HostedServerViewModel CreateViewModel(HostedServer model)
    {
        var vm = new HostedServerViewModel(model, _hosting, _confirm, _dialogs, _logger);
        vm.DeleteRequested += async (_, _) => await DeleteAsync(vm);
        vm.AddModsRequested += (_, target) => AddModsRequested?.Invoke(this, target);
        return vm;
    }

    /// <summary>«Añadir mod» de un servidor: la ventana abre Mods de Explorar para ese servidor.</summary>
    public event EventHandler<ServerInstallTarget>? AddModsRequested;

    /// <summary>Vuelve a la pestaña Contenido de un servidor (desde «Volver al servidor» de Explorar).</summary>
    public void ShowServerContent(string id)
    {
        if (Servers.FirstOrDefault(s => s.Id == id) is not { } server)
            return;
        Selected = server;
        server.Tab = HostedServerTab.Content;
        server.RefreshContentCommand.Execute(null);
    }

    /// <summary>Se instaló algo en un servidor desde Explorar: su lista de contenido se actualiza.</summary>
    public void RefreshServerContent(string id) =>
        Servers.FirstOrDefault(s => s.Id == id)?.RefreshContentCommand.Execute(null);

    /// <summary>Arranca los servidores marcados con «Iniciar al abrir el launcher».</summary>
    public async Task AutoStartAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            Reload();
            _ = LoadMinecraftVersionsAsync();
        }

        foreach (var server in Servers.Where(s => s.Model.AutoStart && s.Model.Installed && s.IsStopped).ToList())
            await server.StartCommand.ExecuteAsync(null);
    }

    public bool AnyRunning => _hosting.AnyRunning;

    /// <summary>Al cerrar el launcher: detiene todos los servidores guardando sus mundos.</summary>
    public Task StopAllAsync() => _hosting.StopAllAsync();

    private async Task DeleteAsync(HostedServerViewModel server)
    {
        var ok = await _confirm.ShowAsync($"Eliminar «{server.Name}»",
            "Se borran el servidor, su mundo, sus mods y plugins, y su configuración. No se puede deshacer.",
            "Eliminar servidor", isDanger: true);
        if (!ok)
            return;
        try
        {
            await _hosting.DeleteAsync(server.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "No se pudo eliminar el servidor {Id}", server.Id);
            server.Error = $"No se pudo eliminar: {ex.Message}";
            return;
        }

        if (Selected == server)
            Selected = null;
        Servers.Remove(server);
        OnPropertyChanged(nameof(HasNoServers));
    }

    // ══════════════════════════════════════════════════════════════════
    // CREAR SERVIDOR
    // ══════════════════════════════════════════════════════════════════

    public ObservableCollection<SoftwareOption> SoftwareOptions { get; }
    public ServerFilter MinecraftVersion { get; }
    public ServerFilter LoaderVersion { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private string _newName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoader), nameof(LoaderLabel))]
    private ServerSoftware _software = ServerSoftware.Vanilla;

    [ObservableProperty] private double _newMemoryMb = Math.Min(4096, SystemMemory.MaxMb);
    [ObservableProperty] private string _newPortText = ServerPropertiesFile.DefaultPort.ToString();
    [ObservableProperty] private string _newMotd = string.Empty;
    [ObservableProperty] private bool _newPremiumOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private bool _acceptEula;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private bool _isLoadingVersions;

    [ObservableProperty] private string? _versionStatus;
    [ObservableProperty] private string? _createError;

    public double MemoryMinMb => SystemMemory.MinMb;
    public double MemoryMaxMb => SystemMemory.MaxMb;
    public double MemoryStepMb => SystemMemory.StepMb;
    public string NewMemoryText => SystemMemory.Format((int)NewMemoryMb);

    partial void OnNewMemoryMbChanged(double value) => OnPropertyChanged(nameof(NewMemoryText));

    public bool HasLoader => Software != ServerSoftware.Vanilla;
    public string LoaderLabel => Software == ServerSoftware.Paper ? "Build de Paper" : $"Versión de {HostedServer.SoftwareName(Software)}";

    public bool CanCreate =>
        NewName.Trim().Length > 0 && AcceptEula && !IsLoadingVersions
        && MinecraftVersion.Value is not null
        && (!HasLoader || LoaderVersion.Value is not null);

    [RelayCommand]
    private void SelectSoftware(SoftwareOption? option)
    {
        if (option is null)
            return;
        foreach (var o in SoftwareOptions)
            o.IsSelected = o == option;
        var changedKind = Software != option.Software || MinecraftVersion.Options.Count == 0;
        // Paper tiene su propia lista de versiones; el resto usa la de Mojang.
        var needsNewList = (Software == ServerSoftware.Paper) != (option.Software == ServerSoftware.Paper);
        Software = option.Software;
        if (!_loaded)
            return;
        if (needsNewList)
            _ = LoadMinecraftVersionsAsync();
        else if (changedKind)
            _ = LoadLoaderVersionsAsync();
    }

    private async Task LoadMinecraftVersionsAsync()
    {
        var request = ++_versionRequest;
        var software = Software;
        var previous = MinecraftVersion.Value;
        IsLoadingVersions = true;
        VersionStatus = null;
        try
        {
            var versions = await Task.Run(() => _catalog.GetMinecraftVersionsAsync(software));
            if (request != _versionRequest)
                return;
            MinecraftVersion.SetOptions(versions.Select(v => ((string?)v, v)), allLabel: null,
                versions.Contains(previous ?? string.Empty) ? previous : versions.FirstOrDefault());
            if (versions.Count == 0)
                VersionStatus = "No se pudo cargar la lista de versiones: revisa la conexión.";
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            if (request == _versionRequest)
                VersionStatus = "No se pudo cargar la lista de versiones: revisa la conexión.";
            _logger.LogWarning(ex, "No se pudieron cargar las versiones de {Software}", software);
        }
        finally
        {
            if (request == _versionRequest)
                IsLoadingVersions = false;
        }

        await LoadLoaderVersionsAsync();
    }

    private void OnMinecraftVersionChanged() => _ = LoadLoaderVersionsAsync();

    private async Task LoadLoaderVersionsAsync()
    {
        var request = ++_loaderRequest;
        LoaderVersion.SetOptions([], allLabel: null);
        OnPropertyChanged(nameof(CanCreate));
        if (!HasLoader || MinecraftVersion.Value is not { } mc)
            return;
        var software = Software;
        VersionStatus = $"Buscando {HostedServer.SoftwareName(software)} para {mc}…";
        try
        {
            var versions = await Task.Run(() => _catalog.GetLoaderVersionsAsync(software, mc));
            if (request != _loaderRequest)
                return;
            var pick = LoaderVersionService.Pick(versions, LoaderChannel.Stable);
            LoaderVersion.SetOptions(versions.Select(v => ((string?)v.Version, v.IsStable ? $"{v.Version} (estable)" : v.Version)), allLabel: null, pick);
            VersionStatus = versions.Count == 0 ? $"{HostedServer.SoftwareName(software)} no está disponible para Minecraft {mc}." : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            if (request == _loaderRequest)
                VersionStatus = "No se pudo consultar las versiones del cargador: revisa la conexión.";
            _logger.LogWarning(ex, "No se pudieron cargar las versiones de {Software} {Mc}", software, mc);
        }
        finally
        {
            OnPropertyChanged(nameof(CanCreate));
        }
    }

    [RelayCommand]
    private void OpenEula()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://aka.ms/MinecraftEULA") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// Crea la carpeta y la configuración y vuelve a la lista: la descarga y la instalación siguen en la fila
    /// del servidor, con su progreso.
    /// </summary>
    [RelayCommand]
    private async Task CreateAsync()
    {
        CreateError = null;
        if (!CanCreate)
            return;
        if (!int.TryParse(NewPortText, out var port) || port is < 1 or > 65535)
        {
            CreateError = "El puerto tiene que ser un número entre 1 y 65535.";
            return;
        }

        HostedServer model;
        try
        {
            model = _hosting.Store.Create(new HostedServer
            {
                Name = NewName,
                Software = Software,
                MinecraftVersion = MinecraftVersion.Value!,
                LoaderVersion = HasLoader ? LoaderVersion.Value : null,
                MemoryMb = (int)NewMemoryMb,
                Publish = new ClientPublishSettings { Port = _hosting.Store.NextFreePublishPort() },
            });

            var dir = _hosting.Store.GetDirectory(model.Id);
            var props = new ServerPropertiesFile();
            props.Set("motd", string.IsNullOrWhiteSpace(NewMotd) ? model.Name : NewMotd.Trim());
            props.Set("server-port", port);
            props.Set("query.port", port);
            // Las cuentas del launcher son sin conexión: por defecto el servidor las acepta.
            props.Set("online-mode", NewPremiumOnly);
            props.Set("max-players", 20);
            // Las versiones recientes traen la lista blanca activada: un servidor para amigos empieza abierto.
            props.Set("white-list", false);
            props.Set("enforce-whitelist", false);
            props.Set("difficulty", "normal");
            props.Set("enable-status", true);
            props.Save(Path.Combine(dir, ServerPropertiesFile.FileName));
            Directory.CreateDirectory(_hosting.Store.GetClientDirectory(model.Id));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            CreateError = ex.Message;
            return;
        }

        var vm = CreateViewModel(model);
        Servers.Insert(0, vm);
        OnPropertyChanged(nameof(HasNoServers));
        NewName = string.Empty;
        NewMotd = string.Empty;
        Tab = HostingTab.Servers;
        await vm.InstallAsync(AcceptEula);
        // Un puerto para el siguiente servidor.
        NewPortText = _hosting.Store.NextFreeGamePort().ToString();
    }

    partial void OnTabChanged(HostingTab value)
    {
        if (value == HostingTab.Create && _loaded)
            NewPortText = _hosting.Store.NextFreeGamePort().ToString();
    }
}
