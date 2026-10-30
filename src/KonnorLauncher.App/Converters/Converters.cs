using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KonnorLauncher.App.Converters;

/// <summary>Texto vacío o null → Collapsed; con texto → Visible (mensajes de error).</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Invierte un bool (para deshabilitar el botón de versiones mientras su popup está abierto).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
