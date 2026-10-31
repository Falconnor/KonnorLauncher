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
}
