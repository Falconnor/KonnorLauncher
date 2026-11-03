using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>
/// Ventana principal sin bordes 1275×700. El código subyacente solo contiene lo puramente visual:
/// arrastre, cerrar/minimizar y la tecla Esc de los overlays.
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

    /// <summary>Tecla / en "Explorar contenido": lleva a la búsqueda (salvo si ya se está escribiendo).</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "/" && _viewModel.IsBrowsing && !AnyOverlayOpen() && Keyboard.FocusedElement is not TextBox)
        {
            BrowsePage.FocusSearch();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
}
