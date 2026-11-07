using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KonnorLauncher.App.Services;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>
/// Ventana principal sin marco, de tamaño libre (como mínimo 1024×768, 4:3, o la pantalla si es menor). El código subyacente solo
/// contiene lo puramente visual: arrastre, los controles de la ventana (minimizar/maximizar/cerrar) y las teclas Esc y /.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// La ventana se arrastra haciendo clic en cualquier zona libre (los botones y campos ya marcan el
    /// evento como manejado, así que no llega aquí). También funciona sobre los overlays.
    /// </summary>
    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Doble clic en una zona libre: maximizar o restaurar, como en la barra de título de Windows.
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove solo es válido con el botón izquierdo pulsado; si ya se soltó, se ignora.
            }
        }
    }

    /// <summary>Esc cierra el overlay abierto como si se pulsara CANCELAR.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        if (_viewModel.IsBrowsing && !AnyOverlayOpen() && _viewModel.Browse.CloseTopmost())
        {
            e.Handled = true;
        }
        else if (_viewModel.IsSkins && !AnyOverlayOpen() && _viewModel.Skins.CloseTopmost())
        {
            e.Handled = true;
        }
        else if (_viewModel.UsernamePrompt.IsOpen)
        {
            _viewModel.UsernamePrompt.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.Confirm.IsOpen)
        {
            _viewModel.Confirm.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.Editor.IsOpen)
        {
            _viewModel.Editor.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.Browse.InstallDialog.IsOpen)
        {
            _viewModel.Browse.InstallDialog.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.Account.IsOpen)
        {
            _viewModel.Account.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (_viewModel.Options.IsOpen)
        {
            _viewModel.Options.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private bool AnyOverlayOpen() =>
        _viewModel.UsernamePrompt.IsOpen || _viewModel.Confirm.IsOpen || _viewModel.Editor.IsOpen
        || _viewModel.Account.IsOpen || _viewModel.Options.IsOpen || _viewModel.Browse.InstallDialog.IsOpen;

    /// <summary>Tecla /: lleva a la búsqueda de Explorar (salvo si ya se está escribiendo).</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "/" && !AnyOverlayOpen() && Keyboard.FocusedElement is not TextBox)
        {
            OpenSearch();
            e.Handled = true;
        }
    }

    private void OpenSearch()
    {
        if (!_viewModel.IsBrowsing)
            _viewModel.OpenBrowseCommand.Execute(null);
        // Cuando la página ya es visible (tras el cambio de pestaña) puede recibir el foco.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, BrowsePage.FocusSearch);
    }

    /// <summary>
    /// Sin pestaña "Instancias", se vuelve de Explorar (o de Skins) con un clic en una instancia. Elegir otra ya lo
    /// hace (cambia la selección); esto cubre la que ya estaba seleccionada.
    /// </summary>
    private void OnInstanceListClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsAwayFromInstance && e.OriginalSource is DependencyObject source
                                  && ItemsControl.ContainerFromElement((ItemsControl)sender, source) is ListBoxItem)
            _viewModel.CloseBrowseCommand.Execute(null);
    }

    /// <summary>
    /// Clic derecho en una instancia: su menú (Jugar, Abrir carpeta, Editar, Eliminar) junto al ratón, sin
    /// cambiar la selección (no se pierde lo que se está viendo).
    /// </summary>
    private void OnInstanceListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement((ItemsControl)sender, source) is not ListBoxItem { DataContext: InstanceItemViewModel item } container)
            return;
        // Nunca cambia la selección. Las filas van pegadas: solo cuenta la franja central, así un clic en el
        // borde entre dos no abre el menú de la vecina.
        e.Handled = true;
        var y = e.GetPosition(container).Y;
        if (y < RightClickEdge || y > container.ActualHeight - RightClickEdge)
            return;
        OpenInstanceMenu(container, item, PlacementMode.MousePoint);
    }

    /// <summary>Margen arriba y abajo de cada fila en el que el clic derecho no abre el menú.</summary>
    private const double RightClickEdge = 10;

    /// <summary>Tecla de menú (o Mayús+F10) sobre una instancia con el foco: el mismo menú, bajo la fila.</summary>
    private void OnInstanceListMenuKey(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        // Con el ratón ya lo abrió OnInstanceListRightClick (sin teclado, CursorLeft y CursorTop valen -1)
        if (e.CursorLeft >= 0 || e.CursorTop >= 0)
            return;
        if (Keyboard.FocusedElement is ListBoxItem { DataContext: InstanceItemViewModel item } container)
            OpenInstanceMenu(container, item, PlacementMode.Bottom);
    }

    private void OpenInstanceMenu(ListBoxItem container, InstanceItemViewModel item, PlacementMode placement)
    {
        var menu = (ContextMenu)FindResource("InstanceMenu");
        menu.DataContext = _viewModel;
        menu.Tag = item;
        menu.PlacementTarget = container;
        menu.Placement = placement;
        menu.IsOpen = true;
    }

    private void OnInstanceListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.IsAwayFromInstance && Keyboard.FocusedElement is ListBoxItem)
        {
            _viewModel.CloseBrowseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Una ventana sin marco, al maximizarse, ocuparía toda la pantalla (tapando la barra de tareas): se
    /// le indica a Windows que su tamaño maximizado es el área de trabajo del monitor.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        // Windows 11: esquinas redondeadas también en la propia ventana (en Windows 10 no hace nada).
        NativeMethods.TryRoundWindowCorners(handle);
        FitToScreen(handle, recenter: true);
        DpiChanged += (_, _) => FitToScreen(handle, recenter: false);
    }

    /// <summary>Tamaño mínimo pensado para la interfaz (4:3), si la pantalla lo permite.</summary>
    private const double DesignMinWidth = 1024;
    private const double DesignMinHeight = 768;

    /// <summary>
    /// En pantallas con poco sitio (un portátil de 1366×768, o 1920×1080 al 150 %) el área de trabajo
    /// es menor que el mínimo de 1024×768: el mínimo y el tamaño inicial se ajustan a ella para que la
    /// ventana no quede cortada (con JUGAR fuera de la pantalla).
    /// </summary>
    private void FitToScreen(nint handle, bool recenter)
    {
        if (!NativeMethods.TryGetWorkArea(handle, out var area))
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (area.Right - area.Left) / dpi.DpiScaleX;
        var height = (area.Bottom - area.Top) / dpi.DpiScaleY;
        MinWidth = Math.Min(DesignMinWidth, width);
        MinHeight = Math.Min(DesignMinHeight, height);
        if (WindowState != WindowState.Normal || (Width <= width && Height <= height))
            return;

        Width = Math.Min(Width, width);
        Height = Math.Min(Height, height);
        if (recenter)
        {
            Left = area.Left / dpi.DpiScaleX + (width - Width) / 2;
            Top = area.Top / dpi.DpiScaleY + (height - Height) / 2;
        }
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmGetMinMaxInfo)
        {
            // El mínimo (1024×768, o el área de trabajo si es menor) en píxeles del monitor, con su escala.
            var dpi = VisualTreeHelper.GetDpi(this);
            NativeMethods.FitMaximizedToWorkArea(hwnd, lParam, (int)Math.Ceiling(MinWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(MinHeight * dpi.DpiScaleY));
            handled = true;
        }

        return 0;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restaurar" : "Maximizar");
    }
}
