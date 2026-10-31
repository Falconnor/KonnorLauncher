using System.Windows;
using System.Windows.Controls;

namespace KonnorLauncher.App.Views;

/// <summary>Diálogo de confirmación (modal, Esc = Cancelar).</summary>
public partial class ConfirmOverlay : UserControl
{
    public ConfirmOverlay() => InitializeComponent();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => CancelButton.Focus());
    }
}
