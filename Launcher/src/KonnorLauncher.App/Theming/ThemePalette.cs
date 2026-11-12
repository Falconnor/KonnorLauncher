using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KonnorLauncher.Core.Theming;

namespace KonnorLauncher.App.Theming;

/// <summary>
/// Colores del tema activo, para los pinceles de <c>Themes/Glass.xaml</c>:
/// <code>&lt;SolidColorBrush x:Key="Accent" Color="{Binding [Accent], Source={x:Static th:ThemePalette.Current}}" /&gt;</code>
/// Las vistas siguen usando <c>{StaticResource Accent}</c>: todas comparten ese mismo pincel, y como su color está
/// enlazado (un pincel enlazado no se congela), cambia en todas a la vez sin <c>DynamicResource</c>. Cambiar de tema
/// = un solo aviso <c>Item[]</c> y un repintado. Los pinceles sin tema (blancos translúcidos, estados) siguen
/// congelados, que es lo más rápido.
/// </summary>
public sealed class ThemePalette : INotifyPropertyChanged
{
    /// <summary>Imagen del launcher (con el nombre del ensamblado: vale aunque lo cargue otro ejecutable).</summary>
    private static readonly Uri DefaultBackgroundUri =
        new($"pack://application:,,,/{typeof(ThemePalette).Assembly.GetName().Name};component/Assets/background.png");

    private Dictionary<string, Color> _colors = Convert(ThemeEngine.Build(ThemeSettings.Default));
    private ImageSource? _background;

    public static ThemePalette Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Color de una clave del tema (las de <see cref="ThemeEngine.Keys"/>); transparente si no existe.</summary>
    public Color this[string key] => _colors.TryGetValue(key, out var color) ? color : Colors.Transparent;

    /// <summary>Imagen de fondo de la ventana: la elegida por el jugador o la del launcher.</summary>
    public ImageSource? Background => _background ??= LoadDefaultBackground();

    /// <summary>Fondo sólido: sin imagen, aurora ni desenfoque (los paneles de cristal se pintan opacos).</summary>
    public bool IsSolid => Settings.Solid;

    /// <summary>Cristal translúcido sobre la imagen de fondo (lo contrario de <see cref="IsSolid"/>).</summary>
    public bool IsTranslucent => !Settings.Solid;

    /// <summary>Tema aplicado ahora mismo.</summary>
    public ThemeSettings Settings { get; private set; } = ThemeSettings.Default;

    /// <summary>Aplica un tema: colores e imagen de fondo. Devuelve false si la imagen propia no se pudo abrir.</summary>
    public bool Apply(ThemeSettings settings)
    {
        var backgroundChanged = !string.Equals(settings.BackgroundPath, Settings.BackgroundPath, StringComparison.OrdinalIgnoreCase)
                                || _background is null;
        var modeChanged = settings.Solid != Settings.Solid;
        Settings = settings;
        _colors = Convert(ThemeEngine.Build(settings));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        if (modeChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSolid)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTranslucent)));
        }

        if (!backgroundChanged)
            return true;
        var custom = settings.BackgroundPath is { } path ? LoadImage(path) : null;
        _background = custom ?? LoadDefaultBackground();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Background)));
        return settings.BackgroundPath is null || custom is not null;
    }

    /// <summary>
    /// Abre una imagen de fondo reducida a lo que se ve (se muestra desenfocada: 1920 px sobran) y congelada,
    /// sin dejar el archivo bloqueado. null si no existe o no es una imagen.
    /// </summary>
    public static BitmapImage? LoadImage(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 1920;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>La imagen del launcher; null si no carga (se ve el degradado de respaldo).</summary>
    private static BitmapImage? LoadDefaultBackground()
    {
        try
        {
            var image = new BitmapImage(DefaultBackgroundUri);
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Dictionary<string, Color> Convert(IReadOnlyDictionary<string, Argb> palette) =>
        palette.ToDictionary(p => p.Key, p => Color.FromArgb(p.Value.A, p.Value.R, p.Value.G, p.Value.B), StringComparer.Ordinal);
}
