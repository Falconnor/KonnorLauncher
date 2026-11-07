using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>Sección Servidores: directorio de Modrinth y Favoritos.</summary>
public partial class ServersView : UserControl
{
    /// <summary>Distancia al final de la lista a la que se piden más servidores.</summary>
    private const double LoadMoreMargin = 400;

    public ServersView() => InitializeComponent();

    /// <summary>Al acercarse al final se cargan más servidores sin tener que pulsar "Cargar más".</summary>
    private void OnServerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || DataContext is not ServersViewModel { HasMore: true, IsLoadingMore: false } viewModel)
            return;
        if (ServerScroll.ScrollableHeight - ServerScroll.VerticalOffset < LoadMoreMargin)
            viewModel.LoadMoreCommand.Execute(null);
    }

    /// <summary>Intro en la dirección o el nombre: añade el servidor.</summary>
    private void OnAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ServersViewModel viewModel)
            return;
        viewModel.AddServerCommand.Execute(null);
        e.Handled = true;
    }
}
