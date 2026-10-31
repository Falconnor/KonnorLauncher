using System.Diagnostics;
using System.Windows;
using KonnorLauncher.App.Views;
using Microsoft.Win32;

namespace KonnorLauncher.App.Services;

/// <summary>Diálogos del sistema y acciones de Windows, separados para que los ViewModels no dependan de la UI.</summary>
public interface IDialogService
{
    string? PickFile(string title, string filter);
    string? PickFolder(string title);
    void OpenInExplorer(string folder);
    void ShowSkinPreview(string? skinPath);
    void ShowWarning(string title, string message);
}

/// <summary>Implementación WPF de <see cref="IDialogService"/>.</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    /// <summary>Diálogo abrir archivo (filtros "Ejecutables (*.exe)|*.exe|Todos (*)|*.*", etc.).</summary>
    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    /// <summary>Diálogo de carpeta nativo (OpenFolderDialog de WPF, .NET 8+).</summary>
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    /// <summary>Abre la carpeta en el Explorador de Windows.</summary>
    public void OpenInExplorer(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });

    /// <summary>Ventana de vista previa de skin (modal, con marco del sistema).</summary>
    public void ShowSkinPreview(string? skinPath)
    {
        var window = new SkinPreviewWindow(skinPath) { Owner = Owner };
        window.ShowDialog();
    }

    public void ShowWarning(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
