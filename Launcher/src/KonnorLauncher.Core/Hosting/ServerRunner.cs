using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KonnorLauncher.Core.Hosting;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>
/// Proceso de un servidor de Minecraft: lo arranca con el Java de Mojang, lee su consola, le envía
/// comandos y lo detiene con <c>stop</c> (guarda el mundo) antes de forzar el cierre. Detecta cuándo
/// termina de arrancar (<c>Done (…)!</c>) y quién entra y sale.
/// </summary>
public sealed partial class ServerRunner : IDisposable
{
    public const int MaxLines = 2000;
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(45);

    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly List<string> _lines = [];
    private readonly SortedSet<string> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private Process? _process;
    private TaskCompletionSource? _exited;

    public ServerRunner(string serverId, ILogger? logger = null)
    {
        ServerId = serverId;
        _logger = logger ?? NullLogger.Instance;
    }

    public string ServerId { get; }
    public ServerState State { get; private set; } = ServerState.Stopped;
    public DateTimeOffset? StartedAt { get; private set; }
    public int? LastExitCode { get; private set; }

    /// <summary>Línea nueva de la consola (en el hilo del proceso).</summary>
    public event EventHandler<string>? LineReceived;
    public event EventHandler<ServerState>? StateChanged;
    public event EventHandler? PlayersChanged;

    public IReadOnlyList<string> Lines
    {
        get { lock (_sync) return [.. _lines]; }
    }

    public IReadOnlyList<string> Players
    {
        get { lock (_sync) return [.. _players]; }
    }

    /// <summary>IP desde la que se conectó <paramref name="player"/> (de su línea «logged in»); null si no se sabe.</summary>
    public string? AddressOf(string player)
    {
        lock (_sync) return _addresses.GetValueOrDefault(player);
    }

    public bool IsAlive => State != ServerState.Stopped;

    /// <summary>
    /// Arranca el servidor. <paramref name="launchArguments"/> es <c>-jar server.jar</c> o el archivo de
    /// argumentos de Forge; se añaden la memoria, UTF-8 en la consola y <c>nogui</c>.
    /// </summary>
    public void Start(string java, string directory, IReadOnlyList<string> launchArguments, int memoryMb)
    {
        lock (_sync)
        {
            if (State != ServerState.Stopped)
                throw new InvalidOperationException("El servidor ya está en marcha");
            _lines.Clear();
            _players.Clear();
            _addresses.Clear();
        }

        var start = new ProcessStartInfo(java)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in BuildArguments(launchArguments, memoryMb))
            start.ArgumentList.Add(arg);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Exited += (_, _) => OnExited(process);

        SetState(ServerState.Starting);
        _exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            process.Start();
        }
        catch
        {
            SetState(ServerState.Stopped);
            _exited.TrySetResult();
            throw;
        }

        _process = process;
        StartedAt = DateTimeOffset.Now;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _logger.LogInformation("Servidor {Id} iniciado (PID {Pid}): {Java} {Args}", ServerId, process.Id, java, string.Join(' ', start.ArgumentList));
    }

    /// <summary>Argumentos de Java: memoria, consola en UTF-8, los del software y <c>nogui</c>.</summary>
    public static List<string> BuildArguments(IReadOnlyList<string> launchArguments, int memoryMb)
    {
        var memory = Math.Max(512, memoryMb);
        return
        [
            $"-Xms{Math.Min(1024, memory)}M",
            $"-Xmx{memory}M",
            "-Dfile.encoding=UTF-8",
            "-Dstdout.encoding=UTF-8",
            "-Dstderr.encoding=UTF-8",
            .. launchArguments,
            "nogui",
        ];
    }

    /// <summary>Envía un comando a la consola (sin la <c>/</c> inicial).</summary>
    public bool SendCommand(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('/'))
            text = text[1..];
        var process = _process;
        if (text.Length == 0 || process is null || State is ServerState.Stopped)
            return false;
        try
        {
            process.StandardInput.WriteLine(text);
            process.StandardInput.Flush();
            AddLine("> " + text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Detiene con <c>stop</c> (guarda el mundo); si no termina a tiempo, fuerza el cierre.</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        var process = _process;
        var exited = _exited;
        if (process is null || exited is null || State == ServerState.Stopped)
            return;
        SetState(ServerState.Stopping);
        if (!SendCommand("stop"))
            Kill();
        var finished = await Task.WhenAny(exited.Task, Task.Delay(timeout ?? StopTimeout)).ConfigureAwait(false);
        if (finished != exited.Task)
        {
            _logger.LogWarning("El servidor {Id} no se detuvo a tiempo: se fuerza el cierre", ServerId);
            Kill();
            await Task.WhenAny(exited.Task, Task.Delay(5000)).ConfigureAwait(false);
        }
    }

    /// <summary>Cierra el proceso sin guardar.</summary>
    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false } process)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Espera a que el proceso termine.</summary>
    public Task WaitForExitAsync() => _exited?.Task ?? Task.CompletedTask;

    private void OnExited(Process process)
    {
        try
        {
            LastExitCode = process.ExitCode;
        }
        catch (InvalidOperationException) { }

        // Las últimas líneas pueden llegar después de Exited: se espera a que se vacíen las tuberías.
        try { process.WaitForExit(); } catch (InvalidOperationException) { }
        AddLine($"[Konnor] El servidor se detuvo (código {LastExitCode}).");
        lock (_sync)
            _players.Clear();
            _addresses.Clear();
        PlayersChanged?.Invoke(this, EventArgs.Empty);
        _process = null;
        process.Dispose();
        SetState(ServerState.Stopped);
        _exited?.TrySetResult();
        _logger.LogInformation("Servidor {Id} detenido (código {Code})", ServerId, LastExitCode);
    }

    private void OnLine(string? raw)
    {
        if (raw is null)
            return;
        var line = Ansi().Replace(raw, string.Empty);
        AddLine(line);

        if (State == ServerState.Starting && DoneLine().IsMatch(line))
            SetState(ServerState.Running);

        if (ParseLogin(line) is { } login)
        {
            lock (_sync)
                _addresses[login.Name] = login.Address;
        }

        if (ParsePlayerEvent(line) is { } evt)
        {
            bool changed;
            lock (_sync)
            {
                changed = evt.Joined ? _players.Add(evt.Name) : _players.Remove(evt.Name);
                if (!evt.Joined)
                    _addresses.Remove(evt.Name);
            }
            if (changed)
                PlayersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AddLine(string line)
    {
        lock (_sync)
        {
            _lines.Add(line);
            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines);
        }

        LineReceived?.Invoke(this, line);
    }

    private void SetState(ServerState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    /// <summary>
    /// <c>[12:00:00] [Server thread/INFO]: Steve joined the game</c> → (Steve, entra). Desde la 26.1 el aviso lleva
    /// delante <c>System chat: </c>. Se ignoran los mensajes de chat (<c>&lt;Steve&gt; … joined the game</c>).
    /// </summary>
    public static (string Name, bool Joined)? ParsePlayerEvent(string line)
    {
        var match = PlayerEvent().Match(line);
        if (!match.Success)
            return null;
        return (match.Groups["name"].Value, match.Groups["what"].Value == "joined");
    }

    /// <summary>
    /// <c>[12:00:00] [Server thread/INFO]: Steve[/1.2.3.4:51234] logged in with entity id 1 at (…)</c> → (Steve, 1.2.3.4).
    /// IPv6 entre corchetes (<c>[/[::1]:51234]</c>) o sin ellos (<c>[/0:0:0:0:0:0:0:1:51234]</c>).
    /// </summary>
    public static (string Name, string Address)? ParseLogin(string line)
    {
        var match = LoginLine().Match(line);
        if (!match.Success)
            return null;
        var address = match.Groups["addr"].Value.Trim('[', ']');
        return (match.Groups["name"].Value, address);
    }

    [GeneratedRegex(@"\]: (?<name>[A-Za-z0-9_]{1,16})\[/(?<addr>[^\]\s]+?|\[[^\]]+\]):\d+\] logged in with entity id")]
    private static partial Regex LoginLine();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"\]: Done \([0-9.,]+s\)!")]
    private static partial Regex DoneLine();

    [GeneratedRegex(@"\]: (?:System chat: )?(?<name>[A-Za-z0-9_]{1,16}) (?<what>joined|left) the game\s*$")]
    private static partial Regex PlayerEvent();

    public void Dispose()
    {
        Kill();
        _process?.Dispose();
    }
}
