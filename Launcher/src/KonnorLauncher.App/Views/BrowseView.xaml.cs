using System.Windows.Controls;
using System.Windows.Input;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>Página "Explorar contenido" (Modrinth).</summary>
public partial class BrowseView : UserControl
{
    private BrowseViewModel? _viewModel;

    public BrowseView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.ResultsReplaced -= OnResultsReplaced;
            _viewModel = DataContext as BrowseViewModel;
            if (_viewModel is not null)
                _viewModel.ResultsReplaced += OnResultsReplaced;
        };
    }

    /// <summary>Resultados nuevos (otra búsqueda u otra página): se vuelve arriba.</summary>
    private void OnResultsReplaced(object? sender, EventArgs e) => ResultsScroll.ScrollToTop();

    /// <summary>Tecla /: lleva a la búsqueda.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }
}
