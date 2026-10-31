// KonnorLauncher.VerifyCli: diagnóstico del servidor sin abrir la interfaz.
// Lee el mismo launcher.properties (junto al ejecutable o con --properties <ruta>), imprime los
// estados en consola y devuelve 0 (éxito) o 1 (fallo).

using KonnorLauncher.Core.Configuration;
using KonnorLauncher.Core.Paths;
using KonnorLauncher.Core.Progress;
using KonnorLauncher.Core.Verification;
using Microsoft.Extensions.Logging;

var dryRun = false;
var verbose = false;
int? blockDownloads = null;
int? extractions = null;
string? propertiesPath = null;

// ── Argumentos ─────────────────────────────────────────────────────────
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dry-run":
            dryRun = true;
            break;
        case "--verbose":
        case "-v":
            verbose = true;
            break;
        case "--descargas-bloques" when i + 1 < args.Length:
            blockDownloads = int.Parse(args[++i]);
            break;
        case "--extracciones" when i + 1 < args.Length:
            extractions = int.Parse(args[++i]);
            break;
        case "--properties" when i + 1 < args.Length:
            propertiesPath = Path.GetFullPath(args[++i]);
            break;
        case "-h":
        case "--help":
            Console.WriteLine("Uso: KonnorLauncher.VerifyCli [--dry-run] [--properties <ruta>] [--verbose] [--descargas-bloques N] [--extracciones N]");
            Console.WriteLine("  --verbose      Muestra reintentos, errores detallados y el log interno");
            Console.WriteLine("  --descargas-bloques N  Bloques que se descargan a la vez (por defecto 2)");
            Console.WriteLine("  --extracciones N       Bloques que se extraen a la vez (por defecto, según los núcleos)");
            Console.WriteLine("  --dry-run      No descarga ni elimina archivos; solo informa qué se repararía");
            Console.WriteLine("  --properties   Ruta a launcher.properties (por defecto, junto al ejecutable)");
            return 0;
        default:
            Console.Error.WriteLine($"Argumento no reconocido: {args[i]}");
            return 2;
    }
}

// ── Configuración ──────────────────────────────────────────────────────
// Las rutas relativas de game.path se resuelven respecto a la carpeta de launcher.properties,
// igual que hace el launcher.
var paths = propertiesPath is null
    ? LauncherPaths.FromCurrentProcess()
    : new LauncherPaths(Path.GetDirectoryName(propertiesPath)!);
var properties = new LauncherProperties(propertiesPath ?? paths.PropertiesPath);
var settings = new LauncherSettings(properties);

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.None)); // sin --verbose solo se imprimen los estados

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var verifier = new ClientVerifier(http, loggerFactory.CreateLogger<ClientVerifier>())
{
    ClientVersionReceived = v => Console.WriteLine($"client_version del servidor: {v}"),
};

// Los estados se imprimen tal cual; las líneas de descarga se sobrescriben en la misma línea.
var lastWasDownload = false;
var progress = new SyncProgress(p =>
{
    if (p.Phase == LauncherPhase.Downloading)
    {
        Console.Write("\r" + p.Message.PadRight(60));
        lastWasDownload = true;
        return;
    }

    if (lastWasDownload)
        Console.WriteLine();
    lastWasDownload = false;
    if (!string.IsNullOrWhiteSpace(p.Message))
        Console.WriteLine(p.Message);
});

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var options = new VerifierOptions
    {
        GameDir = paths.ResolveGameDir(settings.GamePath),
        ServerUrl = settings.ServerUrl,
        EnableVerify = settings.EnableVerify,
        DeleteUnauthorized = settings.DeleteUnauthorized,
        DryRun = dryRun,
    };
    // Paralelismo ajustable para diagnosticar el rendimiento de las descargas.
    if (blockDownloads is { } downloads)
        options = options with { MaxParallelBlockDownloads = downloads };
    if (extractions is { } extractors)
        options = options with { MaxParallelExtractions = extractors };

    var result = await verifier.VerifyAsync(options, new ThrottledProgress(progress), cts.Token);
    return result.Success ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("Cancelado");
    return 1;
}

/// <summary>IProgress síncrono (en consola no hay contexto de UI al que volver).</summary>
internal sealed class SyncProgress(Action<LauncherProgress> action) : IProgress<LauncherProgress>
{
    private readonly object _sync = new();

    public void Report(LauncherProgress value)
    {
        lock (_sync)
            action(value);
    }
}
