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
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != sender; source = VisualTreeHelper.GetParent(source))
        {
            if (source is ButtonBase)
                return;
        }

        _viewModel?.OpenCommand.Execute(server);
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
