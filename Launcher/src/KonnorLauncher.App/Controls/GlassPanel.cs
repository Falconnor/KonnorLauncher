using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using KonnorLauncher.App.Theming;

namespace KonnorLauncher.App.Controls;

/// <summary>
/// Panel de cristal (glassmorphism). WPF no tiene <c>backdrop-filter</c>, así que el panel pinta
/// debajo de su contenido lo que hay detrás de él (<see cref="BackdropProperty"/>, normalmente la
/// imagen de fondo) desenfocado y recortado a su posición exacta, y encima el tinte y el borde.
/// <para>
/// El fondo se toma con un <see cref="VisualBrush"/> cuyo <c>Viewbox</c> se recalcula cuando el panel
/// se mueve o cambia de tamaño, así que el desenfoque siempre coincide con lo que hay detrás.
/// </para>
/// <para>
/// Con el fondo sólido del tema (<see cref="ThemePalette.IsSolid"/>) no se pinta ni se desenfoca nada detrás: el panel
/// es solo su tinte, que entonces es opaco. Así ese modo además ahorra el trabajo del desenfoque.
/// </para>
/// </summary>
public class GlassPanel : ContentControl
{
    /// <summary>
    /// Elemento que se ve desenfocado a través del panel. Se hereda: basta con fijarlo en la raíz.
    /// Nunca debe ser un antecesor del panel (se dibujaría a sí mismo).
    /// </summary>
    public static readonly DependencyProperty BackdropProperty = DependencyProperty.RegisterAttached(
        "Backdrop", typeof(Visual), typeof(GlassPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnBackdropChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(GlassPanel),
        new FrameworkPropertyMetadata(new CornerRadius(20), FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((GlassPanel)d).UpdateClip()));

    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(
        nameof(BlurRadius), typeof(double), typeof(GlassPanel), new PropertyMetadata(36.0, (d, _) => ((GlassPanel)d).UpdateBlur()));

    public static Visual? GetBackdrop(DependencyObject element) => (Visual?)element.GetValue(BackdropProperty);
    public static void SetBackdrop(DependencyObject element, Visual? value) => element.SetValue(BackdropProperty, value);

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double BlurRadius
    {
        get => (double)GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    private readonly VisualBrush _brush = new()
    {
        ViewboxUnits = BrushMappingMode.Absolute,
        Stretch = Stretch.Fill,
        AlignmentX = AlignmentX.Left,
        AlignmentY = AlignmentY.Top,
    };

    private readonly BlurEffect _blur = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
    private Rectangle? _backdropRect;
    private FrameworkElement? _root;
    private Rect _lastViewbox = Rect.Empty;

    public GlassPanel()
    {
        Loaded += (_, _) =>
        {
            LayoutUpdated += OnLayoutUpdated;
            System.ComponentModel.PropertyChangedEventManager.AddHandler(ThemePalette.Current, OnThemeModeChanged, nameof(ThemePalette.IsSolid));
            UpdateBrushVisual();
        };
        Unloaded += (_, _) =>
        {
            LayoutUpdated -= OnLayoutUpdated;
            System.ComponentModel.PropertyChangedEventManager.RemoveHandler(ThemePalette.Current, OnThemeModeChanged, nameof(ThemePalette.IsSolid));
        };
        SizeChanged += (_, _) => { UpdateClip(); UpdateViewbox(); };
        UpdateBlur();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _root = GetTemplateChild("PART_Root") as FrameworkElement;
        _backdropRect = GetTemplateChild("PART_Backdrop") as Rectangle;
        if (_backdropRect is not null)
            _backdropRect.Fill = _brush;

        UpdateBlur();
        UpdateClip();
        UpdateBrushVisual();
    }

    private static void OnBackdropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GlassPanel panel)
            panel.UpdateBrushVisual();
    }

    private void OnThemeModeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateBrushVisual();

    /// <summary>Fondo desenfocado detrás del panel; con el fondo sólido, ninguno (ni pincel ni efecto).</summary>
    private void UpdateBrushVisual()
    {
        var solid = ThemePalette.Current.IsSolid;
        _brush.Visual = solid ? null : GetBackdrop(this);
        if (_backdropRect is not null)
        {
            _backdropRect.Visibility = solid ? Visibility.Collapsed : Visibility.Visible;
            _backdropRect.Effect = solid ? null : _blur;
        }

        _lastViewbox = Rect.Empty;
        UpdateViewbox();
    }

    private void UpdateBlur()
    {
        _blur.Radius = BlurRadius;
        // El rectángulo desenfocado sobresale por los bordes: así el desenfoque no se oscurece en ellos.
        if (_backdropRect is not null)
            _backdropRect.Margin = new Thickness(-BlurRadius);
        _lastViewbox = Rect.Empty;
        UpdateViewbox();
    }

    /// <summary>Recorte con las esquinas redondeadas (el desenfoque no debe salirse del panel).</summary>
    private void UpdateClip()
    {
        if (_root is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var r = CornerRadius.TopLeft;
        _root.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), r, r);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateViewbox();

    /// <summary>Hace que el pincel muestre exactamente la zona del fondo que hay detrás del panel.</summary>
    private void UpdateViewbox()
    {
        if (_brush.Visual is not Visual backdrop || !IsLoaded || ActualWidth <= 0)
            return;
        try
        {
            var margin = BlurRadius;
            var origin = TransformToVisual(backdrop).Transform(new Point(-margin, -margin));
            var viewbox = new Rect(origin, new Size(ActualWidth + 2 * margin, ActualHeight + 2 * margin));
            if (viewbox == _lastViewbox)
                return;
            _lastViewbox = viewbox;
            _brush.Viewbox = viewbox;
        }
        catch (InvalidOperationException)
        {
            // El panel y el fondo aún no comparten árbol visual (durante la carga): se reintenta luego.
        }
    }
}
