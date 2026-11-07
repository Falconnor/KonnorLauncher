using System.Windows.Controls;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>Sección Skins: galería de MineSkin.</summary>
public partial class SkinsView : UserControl
{
    /// <summary>Distancia al final de la lista a la que se piden más skins.</summary>
    private const double LoadMoreMargin = 300;

    public SkinsView() => InitializeComponent();

    /// <summary>Al acercarse al final se cargan más skins sin tener que pulsar "Cargar más".</summary>
    private void OnGalleryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || DataContext is not SkinsViewModel { HasMore: true, IsLoadingMore: false } viewModel)
            return;
        if (GalleryScroll.ScrollableHeight - GalleryScroll.VerticalOffset < LoadMoreMargin)
            viewModel.LoadMoreCommand.Execute(null);
    }
}
