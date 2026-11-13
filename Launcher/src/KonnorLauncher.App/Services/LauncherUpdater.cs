using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using KonnorLauncher.Core.Paths;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;
using Velopack.Windows;

namespace KonnorLauncher.App.Services;

/// <summary>
/// Actualizaciones del launcher con Velopack, desde las releases de GitHub. Solo se <b>comprueba</b> si hay una versión
/// nueva; la descarga empieza cuando el jugador pulsa «Instalar» (<see cref="DownloadAndRestartAsync"/>), y al
/// terminar se aplica y el launcher se reinicia. Velopack descarga solo lo que cambió (delta) cuando puede.
/// Fuera de una instalación de Velopack (al desarrollar, o la versión antigua de Inno Setup) no hace nada.
/// </summary>
public sealed partial class LauncherUpdater : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/Falconnor/KonnorLauncher";

    private readonly UpdateManager _manager;
    private readonly ILogger<LauncherUpdater> _logger;

    public LauncherUpdater(ILogger<LauncherUpdater> logger)
    {
        _logger = logger;
        _manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    }

    /// <summary>Instalado con Velopack: se puede actualizar.</summary>
    public bool IsSupported => _manager.IsInstalled && !_manager.IsPortable;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString() ?? typeof(LauncherUpdater).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Versión nueva encontrada (sin descargar todavía); null = ninguna.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(AvailableVersion), nameof(StatusText))]
    private UpdateInfo? _available;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _error;

    /// <summary>Se comprobó al menos una vez sin error (para decir «estás al día»).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _checked;

    public bool HasUpdate => Available is not null;
    public bool CanInstall => HasUpdate && !IsDownloading;
    public string? AvailableVersion => Available?.TargetFullRelease.Version.ToString();

    public string StatusText =>
        !IsSupported ? "No disponible en esta copia"
        : IsDownloading ? $"Descargando {Progress} %"
        : Error is { } error ? error
        : IsChecking ? "Buscando…"
        : HasUpdate ? $"Versión {AvailableVersion} disponible"
        : Checked ? "Estás en la última versión"
        : string.Empty;

    /// <summary>¿Hay una versión nueva? No descarga nada.</summary>
    public async Task CheckAsync()
    {
        if (!IsSupported || IsChecking || IsDownloading)
            return;
        IsChecking = true;
        Error = null;
        try
        {
            Available = await _manager.CheckForUpdatesAsync().ConfigureAwait(true);
            Checked = true;
            if (Available is { } info)
                _logger.LogInformation("Actualización disponible: {Version}", info.TargetFullRelease.Version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo comprobar si hay actualizaciones");
            Error = "No se pudo comprobar si hay actualizaciones";
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// «Instalar»: descarga la versión nueva y, al terminar, la aplica y reinicia el launcher (Velopack cierra este
    /// proceso). Devuelve solo si falla.
    /// </summary>
    public async Task DownloadAndRestartAsync()
    {
        if (Available is not { } info || IsDownloading)
            return;
        IsDownloading = true;
        Progress = 0;
        Error = null;
        try
        {
            await _manager.DownloadUpdatesAsync(info, p => Progress = p).ConfigureAwait(true);
            _logger.LogInformation("Actualización {Version} descargada: se aplica y se reinicia", info.TargetFullRelease.Version);
            _manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo instalar la actualización");
            Error = "No se pudo descargar la actualización";
            IsDownloading = false;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // DE INNO SETUP A VELOPACK
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Primera vez tras instalar con Velopack: quita la instalación antigua de Inno Setup (en la carpeta de datos), que
    /// si no seguiría en «Aplicaciones» y con su propio acceso directo. Su desinstalador solo borra lo que instaló (el
    /// exe y el icono); las instancias, los mundos y launcher.properties se quedan. Si se instalaron las herramientas
    /// del servidor (panel y gestor), no se toca nada: se desinstalarían con ella. Al terminar se rehacen los accesos
    /// directos (el desinstalador borra los suyos, que se llaman igual).
    /// </summary>
    public async Task RemoveInnoInstallAsync()
    {
        var dir = LauncherPaths.AppDataDir;
        var uninstaller = Path.Combine(dir, "unins000.exe");
        if (!File.Exists(uninstaller))
            return;
        if (Directory.Exists(Path.Combine(dir, "herramientas")))
        {
            _logger.LogInformation("Hay una instalación antigua con las herramientas del servidor: no se desinstala");
            return;
        }

        try
        {
            _logger.LogInformation("Quitando la instalación antigua (Inno Setup)");
            using (Process.Start(new ProcessStartInfo(uninstaller, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = false }))
            {
            }

            // El desinstalador de Inno se copia a una carpeta temporal y sigue desde ahí: se espera a que termine
            // mirando su archivo, no el proceso.
            for (var i = 0; i < 180 && File.Exists(uninstaller); i++)
                await Task.Delay(500).ConfigureAwait(false);

            // Velopack ya los creó al instalar, pero el desinstalador de Inno acaba de borrarlos (se llaman igual):
            // se rehacen. Por eso se usa esta API, que Velopack marca como obsoleta solo porque normalmente no hace falta.
#pragma warning disable CS0618
            new Shortcuts().CreateShortcutForThisExe(ShortcutLocation.Desktop | ShortcutLocation.StartMenuRoot);
#pragma warning restore CS0618
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or InvalidOperationException)
        {
            _logger.LogWarning(ex, "No se pudo quitar la instalación antigua");
        }
    }
}
