using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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

    /// <summary>Tiempo con el ratón encima de una tarjeta antes de pedir su página (no lo que solo se roza).</summary>
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ProjectCard? _hovered;

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
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_hovered is { } card)
                _viewModel?.PrefetchProject(card);
        };
        SizeChanged += (_, e) =>
        {
            IsCompact = e.NewSize.Width < CompactBelow;
            FilterColumn.Width = new GridLength(IsCompact ? 232 : 272);
        };
    }

    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectCard card })
            return;
        _hovered = card;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        _hovered = null;
        _hoverTimer.Stop();
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
