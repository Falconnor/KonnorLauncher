using Velopack;

namespace KonnorLauncher.App;

/// <summary>
/// Punto de entrada. Lo primero es Velopack: al instalar, actualizar o desinstalar, Velopack abre el exe con sus
/// propios argumentos, hace lo suyo y lo cierra antes de que arranque la interfaz. Después, el launcher normal.
/// </summary>
public static class Program
{
    /// <summary>El mismo que pone la ventana (App): así los accesos directos de Velopack se agrupan con ella en la barra de tareas.</summary>
    public const string AppUserModelId = "konnor.launcher.1.0";

    /// <summary>Primera vez tras instalar con Velopack (para quitar la instalación antigua de Inno Setup).</summary>
    public static bool IsFirstRun { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetAppUserModelId(AppUserModelId)
            .OnFirstRun(_ => IsFirstRun = true)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
