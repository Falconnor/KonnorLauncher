using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KonnorLauncher.App.ViewModels;

namespace KonnorLauncher.App.Views;

/// <summary>Contenido de una carpeta (mods, shaders, paquetes…) en lista o como iconos: el de las instancias y el del Hosting.</summary>
public partial class ContentFilesView : UserControl
{
    public static readonly DependencyProperty FilesProperty = DependencyProperty.Register(
        nameof(Files), typeof(ObservableCollection<ModFileItem>), typeof(ContentFilesView));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(ContentFilesView), new PropertyMetadata(string.Empty));

    /// <summary>Color del cubo cuando el archivo no tiene icono (en las instancias, el del cargador).</summary>
    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(ContentFilesView), new PropertyMetadata(null));

    public ContentFilesView() => InitializeComponent();

    public ObservableCollection<ModFileItem>? Files
    {
        get => (ObservableCollection<ModFileItem>?)GetValue(FilesProperty);
        set => SetValue(FilesProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }
}
