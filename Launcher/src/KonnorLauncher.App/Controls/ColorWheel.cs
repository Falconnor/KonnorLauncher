using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KonnorLauncher.Core.Theming;

namespace KonnorLauncher.App.Controls;

/// <summary>
/// Rueda de color: el ángulo es el tono y la distancia al centro la saturación (el borde, el color vivo; el centro,
/// blanco). El brillo oscurece toda la rueda. El círculo es una imagen que se genera una vez por tamaño; al arrastrar
/// solo se redibuja el selector. Con el teclado: ←/→ cambian el tono y ↑/↓ la saturación.
/// </summary>
public sealed class ColorWheel : FrameworkElement
{
    public static readonly DependencyProperty HueProperty = DependencyProperty.Register(
        nameof(Hue), typeof(double), typeof(ColorWheel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(
        nameof(Saturation), typeof(double), typeof(ColorWheel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Brillo (0-1): solo oscurece el dibujo de la rueda; el control no lo cambia.</summary>
    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(
        nameof(Brightness), typeof(double), typeof(ColorWheel), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen ThumbPen = Frozen(new Pen(Brushes.White, 3));
    private static readonly Pen ThumbShadow = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), 5));

    private BitmapSource? _disc;
    private int _discPixels;

    public ColorWheel()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        AutomationProperties.SetName(this, "Rueda de color");
    }

    /// <summary>Tono, de 0 a 360 (0 = rojo, a la derecha; crece en el sentido de las agujas del reloj).</summary>
    public double Hue
    {
        get => (double)GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    /// <summary>Saturación, de 0 (centro, blanco) a 1 (borde, color vivo).</summary>
    public double Saturation
    {
        get => (double)GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    public double Brightness
    {
        get => (double)GetValue(BrightnessProperty);
        set => SetValue(BrightnessProperty, value);
    }

    /// <summary>Ocupa el espacio que le den (160 px si no tiene límite), sin pedir más: si no, se recortaría.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Math.Min(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width,
                            double.IsInfinity(availableSize.Height) ? 160 : availableSize.Height);
        return new Size(size, size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;
        var origin = new Point((ActualWidth - size) / 2, (ActualHeight - size) / 2);
        var center = new Point(origin.X + size / 2, origin.Y + size / 2);
        var radius = size / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // todo el área responde al ratón
        dc.DrawImage(Disc(size), new Rect(origin, new Size(size, size)));
        // Brillo: un velo negro encima (más barato que regenerar la imagen en cada paso del control de brillo)
        var shade = 1 - Math.Clamp(Brightness, 0, 1);
        if (shade > 0.001)
        {
            var veil = new SolidColorBrush(Color.FromArgb((byte)Math.Round(shade * 255), 0, 0, 0));
            veil.Freeze();
            dc.DrawEllipse(veil, null, center, radius, radius);
        }

        var angle = Hue * Math.PI / 180;
        var distance = Math.Clamp(Saturation, 0, 1) * (radius - 2);
        var thumb = new Point(center.X + distance * Math.Cos(angle), center.Y + distance * Math.Sin(angle));
        var color = Argb.FromHsv(Hue, Saturation, Brightness);
        var fill = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        fill.Freeze();
        const double thumbRadius = 9;
        dc.DrawEllipse(null, ThumbShadow, thumb, thumbRadius + 1, thumbRadius + 1);
        dc.DrawEllipse(fill, ThumbPen, thumb, thumbRadius, thumbRadius);
        if (IsKeyboardFocused)
            dc.DrawEllipse(null, ThumbPen, thumb, thumbRadius + 4, thumbRadius + 4);
    }

    /// <summary>El círculo de color a brillo máximo, a la resolución de la pantalla (se reutiliza mientras no cambie el tamaño).</summary>
    private BitmapSource Disc(double size)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var pixels = Math.Max(1, (int)Math.Ceiling(size * dpi.DpiScaleX));
        if (_disc is not null && _discPixels == pixels)
            return _disc;

        var outer = pixels / 2.0;
        var data = new byte[pixels * pixels * 4];
        for (var y = 0; y < pixels; y++)
        {
            for (var x = 0; x < pixels; x++)
            {
                var dx = x + 0.5 - outer;
                var dy = y + 0.5 - outer;
                var r = Math.Sqrt(dx * dx + dy * dy);
                var alpha = Math.Clamp(outer - r, 0, 1); // borde suavizado
                if (alpha <= 0)
                    continue;
                var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var c = Argb.FromHsv(hue, Math.Min(1, r / outer), 1);
                var i = (y * pixels + x) * 4;
                // Bgra32 premultiplicado
                data[i] = (byte)(c.B * alpha);
                data[i + 1] = (byte)(c.G * alpha);
                data[i + 2] = (byte)(c.R * alpha);
                data[i + 3] = (byte)(255 * alpha);
            }
        }

        var bitmap = BitmapSource.Create(pixels, pixels, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null, data, pixels * 4);
        bitmap.Freeze();
        _disc = bitmap;
        _discPixels = pixels;
        return bitmap;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        SetFrom(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)
            SetFrom(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Right:
                Hue = (Hue + 5) % 360;
                break;
            case Key.Left:
                Hue = (Hue + 355) % 360;
                break;
            case Key.Up:
                Saturation = Math.Min(1, Math.Round(Saturation + 0.05, 2));
                break;
            case Key.Down:
                Saturation = Math.Max(0, Math.Round(Saturation - 0.05, 2));
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    /// <summary>Tono y saturación del punto pulsado (fuera del círculo cuenta como el borde).</summary>
    private void SetFrom(Point point)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;
        var dx = point.X - ActualWidth / 2;
        var dy = point.Y - ActualHeight / 2;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var saturation = Math.Clamp(distance / (size / 2 - 2), 0, 1);
        if (distance >= 1)
            Hue = Math.Round((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360);
        Saturation = Math.Round(saturation, 3);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
