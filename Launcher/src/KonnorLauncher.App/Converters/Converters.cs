using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KonnorLauncher.App.Converters;

/// <summary>
/// Texto vacío o null → Collapsed; con texto → Visible (mensajes de error). Con el parámetro
/// <c>invert</c>, al revés (textos de ayuda que se ocultan al escribir).
/// </summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) != Inverted(parameter) ? Visibility.Collapsed : Visibility.Visible;

    internal static bool Inverted(object? parameter) => string.Equals(parameter as string, "invert", StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Invierte un bool (para deshabilitar el botón de versiones mientras su popup está abierto).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Enlaza un enum con un grupo de RadioButton: marcado si el valor es igual al parámetro, y al
/// marcarlo devuelve ese valor (<c>IsChecked="{Binding Loader, ConverterParameter=Fabric}"</c>).
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null → Collapsed; cualquier otro valor → Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Cargador → su color (recursos <c>LoaderVanilla</c>, <c>LoaderFabric</c>...): borde y letras del icono
/// de una instancia.
/// </summary>
public sealed class LoaderBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current.TryFindResource($"Loader{value}") as System.Windows.Media.Brush
        ?? Application.Current.FindResource("Ink3");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 0 → Visible; cualquier otro número → Collapsed (mensaje de lista vacía). Con el parámetro
/// <c>invert</c>, al revés (contadores que solo se ven si hay algo).
/// </summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is 0) != TextToVisibilityConverter.Inverted(parameter) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
