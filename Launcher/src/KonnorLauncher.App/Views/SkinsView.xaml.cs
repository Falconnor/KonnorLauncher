using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>Sección Skins: galería de MineSkin.</summary>
public partial class SkinsView : UserControl
{
    /// <summary>Distancia al final de la lista a la que se piden más skins.</summary>
    private const double LoadMoreMargin = 300;

    /// <summary>Zoom que mueve cada paso de la rueda sobre el deslizador.</summary>
    private const double WheelStep = 0.05;

    /// <summary>Tiempo que se ve el % después del último cambio.</summary>
    private static readonly TimeSpan BadgeVisible = TimeSpan.FromMilliseconds(1100);

    private readonly DispatcherTimer _badgeTimer;

    public SkinsView()
    {
        InitializeComponent();
        _badgeTimer = new DispatcherTimer { Interval = BadgeVisible };
        _badgeTimer.Tick += (_, _) =>
        {
            _badgeTimer.Stop();
            FadeBadge(0, 250);
        };
    }

    /// <summary>Al acercarse al final se cargan más skins sin tener que pulsar "Cargar más".</summary>
    private void OnGalleryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || DataContext is not SkinsViewModel { HasMore: true, IsLoadingMore: false } viewModel)
            return;
        if (GalleryScroll.ScrollableHeight - GalleryScroll.VerticalOffset < LoadMoreMargin)
            viewModel.LoadMoreCommand.Execute(null);
    }

    // ── Deslizador de zoom ──────────────────────────────────────────────────

    /// <summary>Rueda con el ratón sobre el deslizador: hacia arriba acerca, hacia abajo aleja.</summary>
    private void OnZoomWheel(object sender, MouseWheelEventArgs e)
    {
        var slider = (Slider)sender;
        slider.Value = Math.Clamp(slider.Value + e.Delta / 120.0 * WheelStep, slider.Minimum, slider.Maximum);
        e.Handled = true; // que no desplace la página
    }

    /// <summary>Con cada cambio (rueda o arrastre) se ve un momento el % junto al tirador.</summary>
    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var slider = (Slider)sender;
        if (!slider.IsLoaded || !slider.IsVisible)
            return; // al abrir la ficha o al enlazar el valor: sin aviso
        ZoomBadgeText.Text = $"{Math.Round(e.NewValue / SkinsViewModel.DefaultViewerZoom * 100)} %";
        // A la altura del tirador. Se calcula por el valor: el tirador aún no se ha movido en este momento.
        if (slider.Template.FindName("PART_Track", slider) is Track { Thumb: { } thumb } track && slider.Maximum > slider.Minimum)
        {
            ZoomBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var fraction = (e.NewValue - slider.Minimum) / (slider.Maximum - slider.Minimum);
            var top = track.TranslatePoint(new Point(0, 0), slider).Y;
            var center = top + (1 - fraction) * (track.ActualHeight - thumb.ActualHeight) + thumb.ActualHeight / 2;
            Canvas.SetTop(ZoomBadge, center - ZoomBadge.DesiredSize.Height / 2);
        }

        FadeBadge(1, 120);
        _badgeTimer.Stop();
        _badgeTimer.Start();
    }

    private void FadeBadge(double to, int milliseconds) =>
        ZoomBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)));
}
