using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using KonnorLauncher.App.Services;
using KonnorLauncher.App.ViewModels;
using KonnorLauncher.App.Views;
using KonnorLauncher.Core;
using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Hosting;
using KonnorLauncher.Core.Instances;
using KonnorLauncher.Core.Launching;
using KonnorLauncher.Core.Loaders;
using KonnorLauncher.Core.Modrinth;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Players;
using KonnorLauncher.Core.Servers;
using KonnorLauncher.Core.Versions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace KonnorLauncher.App;

/// <summary>
/// Punto de entrada. Prepara todo antes de mostrar la ventana: AppUserModelID, instancia única,
/// logs locales, manejo global de excepciones y el host con inyección de dependencias.
/// </summary>
public partial class App : Application
{
    private const string AppUserModelId = Program.AppUserModelId;
    private const string SingleInstanceMutexName = @"Local\KonnorLauncher.SingleInstance";

    private IHost? _host;
    private Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── AppUserModelID antes de crear cualquier ventana (agrupa igual en la barra de tareas) ──
        _ = NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        // ── Instancia única: si ya hay una abierta, se activa su ventana y se sale ──
        // Una sola copia por carpeta de datos: dos launchers sobre los mismos archivos se pisarían.
        // Con otra carpeta (KONNOR_DATA_DIR, para pruebas o una instalación portátil) sí puede haber dos.
        var mutexName = LauncherPaths.DataDir == LauncherPaths.AppDataDir
            ? SingleInstanceMutexName
            : $"{SingleInstanceMutexName}.{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(LauncherPaths.DataDir.ToUpperInvariant())))[..12]}";
        _singleInstance = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        // ── Logging local (Serilog, rotación diaria, 7 días). Nada sale del equipo. ──
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning) // la URL de cada petición no aporta nada al log
            .MinimumLevel.Override("Microsoft.Hosting", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(LauncherPaths.LogsDir, "launcher-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        RegisterGlobalExceptionHandlers();

        // ── Host genérico: DI, HttpClient y logging ──
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true, // sin appsettings ni variables de entorno: la config vive en launcher.properties
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(dispose: true);
        ConfigureServices(builder.Services);
        _host = builder.Build();
        await _host.StartAsync();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        var paths = _host.Services.GetRequiredService<LauncherPaths>();
        logger.LogInformation("Konnor Launcher {Version} iniciado. Carpeta: {Dir}. Datos: {Data}",
            typeof(App).Assembly.GetName().Version, paths.LaunchDir, LauncherPaths.DataDir);

        // Primera vez con instancias: la configuración de launcher.properties pasa a la instancia "Konnor".
        try
        {
            _host.Services.GetRequiredService<InstanceStore>().MigrateLegacy(_host.Services.GetRequiredService<LauncherSettings>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "No se pudo migrar la configuración antigua a una instancia");
        }

        // Tema guardado (Opciones → Tema) antes de crear la ventana: así se abre ya con sus colores.
        if (!Theming.ThemePalette.Current.Apply(_host.Services.GetRequiredService<LauncherSettings>().Theme))
            logger.LogWarning("No se pudo abrir la imagen de fondo del tema; se usa la del launcher");

        // Iconos de shaders, paquetes y mods sin icono propio: se buscan en Modrinth por su hash.
        ContentIconResolver.Current = _host.Services.GetRequiredService<ContentIconResolver>();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // La lista de versiones de Mojang, los manifests de las instancias sincronizadas y la primera página
        // de Explorar (así se abre al instante) se piden en segundo plano para no retrasar el arranque.
        var main = _host.Services.GetRequiredService<MainViewModel>();
        // Recién instalado con Velopack: fuera la instalación antigua de Inno Setup (si la hay).
        if (Program.IsFirstRun)
            _ = Task.Run(() => main.Updater.RemoveInnoInstallAsync());
        await Task.WhenAll(main.LoadRemoteVersionsAsync(), main.CheckLinkedInstancesAsync(), main.Browse.PrefetchAsync(), main.Hosting.AutoStartAsync(),
            main.CheckLauncherUpdateAsync());
    }

    /// <summary>Registro de servicios (Core + ViewModels + vistas).</summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // Configuración y rutas (launcher.properties junto al exe; instalado con Velopack, en la carpeta de datos).
        // Instalado con Velopack, launcher.properties va en la carpeta de datos (la del programa se reemplaza al actualizar).
        services.AddSingleton(_ =>
        {
            var paths = LauncherPaths.FromCurrentProcess();
            try
            {
                paths.EnsurePropertiesFile();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "No se pudo crear launcher.properties en la carpeta de datos");
            }

            return paths;
        });
        services.AddSingleton(sp => new LauncherProperties(sp.GetRequiredService<LauncherPaths>().PropertiesPath));
        services.AddSingleton<LauncherUpdater>();
        services.AddSingleton<LauncherSettings>();

        // HttpClient sin timeout global: los timeouts se controlan por petición y por lectura en el Core.
        var userAgent = $"KonnorLauncher/{typeof(App).Assembly.GetName().Version?.ToString(3)}";
        services.AddHttpClient(PlayWorkflow.ServerHttpClientName, c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });
        services.AddHttpClient(GameLauncher.HttpClientName, c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });
        // Fabric, Quilt, Forge y NeoForge: listas de versiones e instaladores.
        services.AddHttpClient(LoaderVersionService.HttpClientName, c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });

        // Galería de skins (MineSkin) y sus texturas: también piden identificar el programa.
        services.AddHttpClient(MineSkinClient.HttpClientName, c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

        // Estado en vivo de «Mis servidores» (mcstatus.io).
        services.AddHttpClient(ServerStatusClient.HttpClientName, c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

        // IP pública para la sección Hosting (api.ipify.org devuelve solo la IP en texto).
        services.AddHttpClient(NetworkAccess.HttpClientName, c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });

        // Modrinth: pide identificar el programa en el User-Agent (sin datos del jugador).
        services.AddHttpClient(ModrinthClient.HttpClientName, c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            // Formato que pide Modrinth (usuario/proyecto/versión); .NET no lo acepta como "producto", así que va sin validar.
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                $"Falconnor/KonnorLauncher/{typeof(App).Assembly.GetName().Version?.ToString(3)} (github.com/Falconnor/KonnorLauncher)");
        })
        // Respuestas comprimidas (gzip/brotli): una página de resultados pasa de 37 KB a 8 KB y las
        // versiones de un mod grande, de más de 1 MB a unos 200 KB.
        .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

        // Core. Las instancias viven en %APPDATA%\KonnorLauncher\instances.
        services.AddSingleton(sp => new InstanceStore(LauncherPaths.InstancesDir, sp.GetRequiredService<ILogger<InstanceStore>>()));
        services.AddSingleton<InstanceSync>();
        services.AddSingleton<VersionService>();
        services.AddSingleton<LoaderVersionService>();
        services.AddSingleton<LoaderInstaller>();
        services.AddSingleton<GameLauncher>();
        services.AddSingleton<PlayWorkflow>();
        services.AddSingleton(sp => new ModrinthClient(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(), sp.GetRequiredService<ILogger<ModrinthClient>>()));
        services.AddSingleton<ContentInstaller>();
        services.AddSingleton(sp => new MineSkinClient(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(), sp.GetRequiredService<ILogger<MineSkinClient>>()));
        services.AddSingleton(sp => new SkinLibrary(System.IO.Path.Combine(LauncherPaths.PlayerDir, "skins"), sp.GetRequiredService<ILogger<SkinLibrary>>()));
        services.AddSingleton<IconLoader>();
        services.AddSingleton<ContentIconResolver>();
        // Servidores: directorio de Modrinth (v3), estado en vivo y lo que recuerda el launcher (Mis servidores).
        services.AddSingleton(sp => new ServerDirectoryClient(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(), sp.GetRequiredService<ILogger<ServerDirectoryClient>>()));
        services.AddSingleton(sp => new ServerStatusClient(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(), sp.GetRequiredService<ILogger<ServerStatusClient>>()));
        services.AddSingleton(sp => new ServerLibrary(LauncherPaths.DataDir, sp.GetRequiredService<ILogger<ServerLibrary>>()));
        // Hosting: servidores alojados en este equipo (en %APPDATA%\KonnorLauncher\hosting) y publicación del cliente.
        services.AddSingleton(sp => new HostedServerStore(System.IO.Path.Combine(LauncherPaths.DataDir, "hosting"), sp.GetRequiredService<ILogger<HostedServerStore>>()));
        services.AddSingleton(sp => new ServerInstaller(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(), LauncherPaths.MetaDir, sp.GetRequiredService<ILogger<ServerInstaller>>()));
        services.AddSingleton<ServerSoftwareCatalog>();
        services.AddSingleton<NetworkAccess>();
        services.AddSingleton<HostingService>();

        // UI.
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<OptionsViewModel>();
        services.AddSingleton<AccountViewModel>();
        services.AddSingleton<InstallDialogViewModel>();
        services.AddSingleton<BrowseViewModel>();
        services.AddSingleton<SkinsViewModel>();
        services.AddSingleton<ServersViewModel>();
        services.AddSingleton<HostingViewModel>();
        services.AddSingleton<UsernamePromptViewModel>();
        services.AddSingleton<InstanceEditorViewModel>();
        services.AddSingleton<ConfirmViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    /// <summary>Cualquier error no controlado se guarda en el log y se muestra (sin cerrar si es recuperable).</summary>
    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Excepción no controlada en la UI");
            MessageBox.Show($"Se produjo un error inesperado:\n{args.Exception.Message}\n\nDetalles en la carpeta de logs.",
                "Konnor Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Excepción no controlada (terminando: {Terminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Excepción de tarea no observada");
            args.SetObserved();
        };
    }

    /// <summary>Si el launcher ya estaba abierto, trae su ventana al frente en vez de abrir otro.</summary>
    private static void ActivateExistingInstance()
    {
        var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id || process.MainWindowHandle == 0)
                continue;
            if (NativeMethods.IsIconic(process.MainWindowHandle))
                NativeMethods.ShowWindow(process.MainWindowHandle, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(process.MainWindowHandle);
            break;
        }
    }

    /// <summary>Cierra todo en orden y de forma síncrona (WPF no espera a un OnExit async): host, logs y mutex.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            Task.Run(() => _host.StopAsync()).Wait(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        Log.CloseAndFlush();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
