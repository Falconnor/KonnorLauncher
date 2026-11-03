using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>Overlay OPCIONES: capa dentro de la ventana (modal, Esc = Cancelar).</summary>
public partial class OptionsOverlay : UserControl
{
    public OptionsOverlay() => InitializeComponent();

    /// <summary>Al abrir, el foco entra en el panel para que Tab no salga a la ventana de debajo.</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
    }
}
