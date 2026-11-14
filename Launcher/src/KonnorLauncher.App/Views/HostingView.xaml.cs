using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KonnorLauncher.App.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KonnorLauncher.App.Views;

public partial class HostingView : UserControl
{
    private HostingViewModel? _viewModel;
    private HostedServerViewModel? _watched;

    public HostingView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as HostingViewModel);
        // Fuera de la sección no se consulta exaroton cada pocos segundos; al volver, sí.
        IsVisibleChanged += (_, e) =>
        {
            if (_viewModel is null)
                return;
            if (e.NewValue is true && _viewModel.IsThirdPartyTab)
                _viewModel.ThirdParty.Resume();
            else if (e.NewValue is false)
                _viewModel.ThirdParty.Pause();
        };
    }

    private void Attach(HostingViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.ThirdParty.PropertyChanged -= OnThirdPartyChanged;
            _viewModel.ThirdParty.ConsoleUpdated -= OnThirdPartyConsole;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.ThirdParty.PropertyChanged += OnThirdPartyChanged;
            _viewModel.ThirdParty.ConsoleUpdated += OnThirdPartyConsole;
        }

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

    // ══════════════════════════════════════════════════════════════════
    // TERCEROS: consola de exaroton y panel web integrado
    // ══════════════════════════════════════════════════════════════════

    private void OnThirdPartyConsole(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ThirdPartyConsole.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>Intro en la caja de comandos de exaroton: lo envía.</summary>
    private void OnThirdPartyCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel?.ThirdParty.SendCommandCommand is not { } command)
            return;
        if (command.CanExecute(null))
            command.Execute(null);
        e.Handled = true;
    }

    private void OnThirdPartyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThirdPartyViewModel.PanelUri))
            _ = ShowPanelAsync(_viewModel?.ThirdParty.PanelUri);
    }

    // Entorno propio para los paneles (con scripts y su sesión), aparte del de las páginas de Modrinth.
    private static Task<CoreWebView2Environment>? _panelEnvironment;
    private WebView2CompositionControl? _panel;
    private int _panelGeneration;

    /// <summary>
    /// Abre el panel web del hosting dentro del launcher. La sesión (cookies) se guarda en su carpeta: el jugador inicia
    /// sesión una vez. Sin <paramref name="uri"/>, se cierra y libera el navegador.
    /// </summary>
    private async Task ShowPanelAsync(Uri? uri)
    {
        var generation = ++_panelGeneration;
        if (uri is null)
        {
            ClosePanel();
            return;
        }

        ThirdPartyPanelMessage.Text = "Cargando el panel…";
        ThirdPartyPanelMessage.Visibility = Visibility.Visible;
        try
        {
            if (_panel is null)
            {
                _panelEnvironment ??= CoreWebView2Environment.CreateAsync(null,
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KonnorLauncher", "webview-paneles"));
                var web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 14, 19) };
                _panel = web;
                ThirdPartyPanelHost.Child = null;
                var grid = new Grid();
                grid.Children.Add(web);
                grid.Children.Add(ThirdPartyPanelMessage);
                ThirdPartyPanelHost.Child = grid;
                await web.EnsureCoreWebView2Async(await _panelEnvironment);
                if (generation != _panelGeneration)
                    return;
                var core = web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                // Ventanas nuevas (inicios de sesión, enlaces): en el mismo panel, sin abrir otra ventana.
                core.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) && target.Scheme is "https" or "http")
                        core.Navigate(target.AbsoluteUri);
                };
                core.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess)
                        ThirdPartyPanelMessage.Visibility = Visibility.Collapsed;
                    else
                        ThirdPartyPanelMessage.Text = "No se pudo cargar el panel. Revisa la conexión o ábrelo en el navegador.";
                };
            }

            _panel.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException
                                       or UnauthorizedAccessException)
        {
            ThirdPartyPanelMessage.Text = "No se puede mostrar el panel aquí (falta Microsoft Edge WebView2). Ábrelo en el navegador.";
            ThirdPartyPanelMessage.Visibility = Visibility.Visible;
        }
    }

    private void ClosePanel()
    {
        if (_panel is null)
            return;
        // El mensaje vuelve a ser el contenido del marco (antes hay que sacarlo de la cuadrícula del navegador).
        if (ThirdPartyPanelHost.Child is Grid grid)
            grid.Children.Clear();
        ThirdPartyPanelHost.Child = ThirdPartyPanelMessage;
        _panel.Dispose();
        _panel = null;
    }

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
