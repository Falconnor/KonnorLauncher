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
using KonnorLauncher.Core.Launching;
using KonnorLauncher.Core.Paths;
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
    private const string AppUserModelId = "konnor.launcher.1.0";
    private const string SingleInstanceMutexName = @"Local\KonnorLauncher.SingleInstance";

    private IHost? _host;
    private Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── AppUserModelID antes de crear cualquier ventana (agrupa igual en la barra de tareas) ──
        _ = NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        // ── Instancia única: si ya hay una abierta, se activa su ventana y se sale ──
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
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
        EnsureMainFont();

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
        logger.LogInformation("Konnor Launcher {Version} iniciado. Carpeta: {Dir}",
            typeof(App).Assembly.GetName().Version, paths.LaunchDir);

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // La lista de versiones de Mojang se pide en segundo plano para no retrasar el arranque.
        await _host.Services.GetRequiredService<MainViewModel>().LoadRemoteVersionsAsync();
    }

    /// <summary>Registro de servicios (Core + ViewModels + vistas).</summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // Configuración y rutas (launcher.properties vive junto al exe).
        services.AddSingleton(_ => LauncherPaths.FromCurrentProcess());
        services.AddSingleton(sp => new LauncherProperties(sp.GetRequiredService<LauncherPaths>().PropertiesPath));
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

        // Core.
        services.AddSingleton<VersionService>();
        services.AddSingleton<GameLauncher>();
        services.AddSingleton<PlayWorkflow>();

        // UI.
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<OptionsViewModel>();
        services.AddSingleton<UsernamePromptViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    /// <summary>
    /// Comprueba que "Boring Time" se cargó desde los recursos; si no, usa Arial.
    /// </summary>
    private void EnsureMainFont()
    {
        try
        {
            var families = Fonts.GetFontFamilies(new Uri("pack://application:,,,/Assets/"));
            if (families.Any(f => f.Source.EndsWith("#Boring Time", StringComparison.OrdinalIgnoreCase)
                                  || f.FamilyNames.Values.Contains("Boring Time")))
                return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "No se pudo enumerar las fuentes incrustadas");
        }

        Log.Warning("Fuente 'Boring Time' no disponible; se usa Arial");
        Resources["MainFont"] = new FontFamily("Arial");
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
