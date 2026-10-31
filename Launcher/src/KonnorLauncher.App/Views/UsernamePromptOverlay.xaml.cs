using System.Windows;
using System.Windows.Controls;

namespace KonnorLauncher.App.Views;

/// <summary>Overlay ESCRIBE TU NOMBRE (modal, Enter = Guardar, Esc = Cancelar).</summary>
public partial class UsernamePromptOverlay : UserControl
{
    public UsernamePromptOverlay() => InitializeComponent();

    /// <summary>Al aparecer, el cursor se coloca directamente en el campo del nombre.</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => NameBox.Focus());
    }
}
