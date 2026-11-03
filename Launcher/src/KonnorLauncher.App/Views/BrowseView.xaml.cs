using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>Página "Explorar contenido" (Modrinth).</summary>
public partial class BrowseView : UserControl
{
    /// <summary>Por debajo de este ancho la página usa su versión compacta (ventana estrecha).</summary>
    private const double CompactBelow = 820;

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(BrowseView), new PropertyMetadata(false));

    /// <summary>
    /// Ventana estrecha: columna de filtros más fina, pestañas solo con icono (salvo la elegida), las
    /// acciones de cada tarjeta en una fila debajo y solo el paginador de abajo.
    /// </summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

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
        SizeChanged += (_, e) =>
        {
            IsCompact = e.NewSize.Width < CompactBelow;
            FilterColumn.Width = new GridLength(IsCompact ? 232 : 272);
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
