namespace KonnorLauncher.Core.Progress;

/// <summary>Fase del flujo de JUGAR; la UI la usa para el texto del botón.</summary>
public enum LauncherPhase
{
    Idle,
    Verifying,
    Downloading,
    Extracting,
    Installing,
    Launching,
    Running,
}

/// <summary>
/// Progreso tipado que se reporta a la UI (antes era un texto del tipo <c>MC_PROGRESS|...</c>).
/// <paramref name="Percent"/> con valor → barra visible; null → barra oculta y solo texto.
/// </summary>
public sealed record LauncherProgress(LauncherPhase Phase, string Message, double? Percent = null);

/// <summary>
/// Envuelve un <see cref="IProgress{T}"/> y limita la frecuencia de reportes (por defecto 100 ms)
/// para no saturar el dispatcher de la UI. Los reportes sin porcentaje, los de 100 % y los cambios
/// de fase se envían siempre.
/// </summary>
public sealed class ThrottledProgress : IProgress<LauncherProgress>
{
    private readonly IProgress<LauncherProgress>? _inner;
    private readonly long _intervalTicks;
    private readonly object _sync = new();
    private long _lastTimestamp;
    private LauncherPhase? _lastPhase;

    public ThrottledProgress(IProgress<LauncherProgress>? inner, TimeSpan? interval = null)
    {
        _inner = inner;
        _intervalTicks = (long)((interval ?? TimeSpan.FromMilliseconds(100)).TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
    }

    public void Report(LauncherProgress value)
    {
        if (_inner is null)
            return;
        lock (_sync)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var force = value.Percent is null || value.Percent >= 100 || value.Phase != _lastPhase;
            if (!force && now - _lastTimestamp < _intervalTicks)
                return;
            _lastTimestamp = now;
            _lastPhase = value.Phase;
        }

        _inner.Report(value);
    }
}
