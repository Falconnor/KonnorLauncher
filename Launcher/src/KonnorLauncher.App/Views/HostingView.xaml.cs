using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

public partial class HostingView : UserControl
{
    private HostingViewModel? _viewModel;
    private HostedServerViewModel? _watched;

    public HostingView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as HostingViewModel);
    }

    private void Attach(HostingViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = viewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelChanged;
        Watch(_viewModel?.Selected);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostingViewModel.Selected))
            Watch(_viewModel?.Selected);
    }

    /// <summary>La consola del servidor abierto baja sola hasta la última línea (si ya se estaba al final).</summary>
    private void Watch(HostedServerViewModel? server)
    {
        if (_watched is not null)
            _watched.ConsoleAppended -= OnConsoleAppended;
        _watched = server;
        if (server is not null)
        {
            server.ConsoleAppended += OnConsoleAppended;
            Dispatcher.BeginInvoke(() => ScrollConsoleToEnd(force: true), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnConsoleAppended(object? sender, EventArgs e) => ScrollConsoleToEnd(force: false);

    private void ScrollConsoleToEnd(bool force)
    {
        if (FindScrollViewer(ConsoleList) is not { } scroll)
            return;
        // Con CanContentScroll la posición va en elementos: «al final» = a menos de 3 líneas.
        if (force || scroll.VerticalOffset >= scroll.ScrollableHeight - 3)
            scroll.ScrollToEnd();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>Pulsar una fila (fuera de sus botones) abre el servidor.</summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement { DataContext: HostedServerViewModel server })
            return;
        if (IsInsideButton(e.OriginalSource as DependencyObject, sender))
            return;

        _viewModel?.OpenCommand.Execute(server);
    }

    /// <summary>El clic salió de un botón de la fila (entonces no abre nada).</summary>
    private static bool IsInsideButton(DependencyObject? source, object row)
    {
        for (; source is not null && source != row; source = ParentOf(source))
        {
            if (source is ButtonBase)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Padre de un elemento. Un <see cref="System.Windows.Documents.Run"/> (texto con formato) no es visual:
    /// VisualTreeHelper falla con él, su padre es el lógico.
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject child) =>
        child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);

    /// <summary>Pulsar un jugador (fuera de sus botones) abre su perfil.</summary>
    private void OnPlayerRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement { DataContext: PlayerEntry player } row)
            return;
        if (IsInsideButton(e.OriginalSource as DependencyObject, sender))
            return;

        for (DependencyObject? parent = row; parent is not null; parent = ParentOf(parent))
        {
            if (parent is ItemsControl { DataContext: HostedServerViewModel server })
            {
                server.OpenPlayerCommand.Execute(player);
                return;
            }
        }
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.IsCreateTab = true;
    }

    /// <summary>Enter envía el comando; flechas: comandos anteriores.</summary>
    private void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel?.Selected is not { } server)
            return;
        switch (e.Key)
        {
            case Key.Enter:
                if (server.SendCommandCommand.CanExecute(null))
                    server.SendCommandCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                server.HistoryStep(-1);
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Down:
                server.HistoryStep(1);
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
        }
    }

    private void OnCopyConsole(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.Selected is not { } server)
            return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, server.Console.Select(l => l.Text)));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }
}
