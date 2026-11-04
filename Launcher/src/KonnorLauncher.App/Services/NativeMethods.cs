using System.Runtime.InteropServices;

namespace KonnorLauncher.App.Services;

/// <summary>P/Invoke de Windows usados por el launcher.</summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// Fija el AppUserModelID del proceso (<c>konnor.launcher.1.0</c>) para que la
    /// barra de tareas agrupe e identifique el launcher. Debe llamarse antes de crear ventanas.
    /// </summary>
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    internal const int SwRestore = 9;

    // ── Esquinas redondeadas de la ventana (Windows 11) ─────────────────────

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);

    /// <summary>
    /// Pide a Windows 11 que redondee las esquinas de la ventana. En Windows 10 el atributo no existe:
    /// la llamada falla sin consecuencias y la ventana sigue rectangular.
    /// </summary>
    internal static void TryRoundWindowCorners(nint hWnd)
    {
        var preference = DwmwcpRound;
        try
        {
            _ = DwmSetWindowAttribute(hWnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
    }

    // ── Tamaño máximo de la ventana sin marco (WM_GETMINMAXINFO) ──────────

    internal const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hWnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    /// <summary>Área de trabajo (sin la barra de tareas), en píxeles, del monitor en el que está la ventana.</summary>
    internal static bool TryGetWorkArea(nint hWnd, out Rect workArea)
    {
        workArea = default;
        var monitor = MonitorFromWindow(hWnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
            return false;
        workArea = info.Work;
        return true;
    }

    /// <summary>
    /// Maximizada, la ventana ocupa el área de trabajo del monitor en el que está (sin tapar la barra de
    /// tareas) y no puede ser más pequeña que <paramref name="minWidth"/>×<paramref name="minHeight"/> píxeles.
    /// </summary>
    internal static void FitMaximizedToWorkArea(nint hWnd, nint lParam, int minWidth, int minHeight)
    {
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hWnd, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfoW(monitor, ref monitorInfo))
        {
            info.MaxPosition.X = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
            info.MaxPosition.Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
            info.MaxSize.X = monitorInfo.Work.Right - monitorInfo.Work.Left;
            info.MaxSize.Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
        }

        info.MinTrackSize.X = minWidth;
        info.MinTrackSize.Y = minHeight;
        Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
    }
}
