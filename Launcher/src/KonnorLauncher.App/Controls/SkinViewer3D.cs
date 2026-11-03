using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using KonnorLauncher.Core.Players;

namespace KonnorLauncher.App.Controls;

/// <summary>
/// Visor 3D de una skin de Minecraft: el jugador con sus capas base y exteriores, girando despacio.
/// Se arrastra con el ratón para girarlo. La textura debe venir ya normalizada a 64×64
/// (<see cref="SkinTexture.Normalize"/>). Las coordenadas de cada caja siguen el diseño de las skins
/// del juego: arriba, abajo, lado derecho, frente, lado izquierdo y espalda.
/// </summary>
public sealed class SkinViewer3D : Grid
{
    public static readonly DependencyProperty SkinProperty = DependencyProperty.Register(
        nameof(Skin), typeof(SkinComposer.Pixels), typeof(SkinViewer3D), new PropertyMetadata(null, (d, _) => ((SkinViewer3D)d).Rebuild()));

    public static readonly DependencyProperty SlimProperty = DependencyProperty.Register(
        nameof(Slim), typeof(bool), typeof(SkinViewer3D), new PropertyMetadata(false, (d, _) => ((SkinViewer3D)d).Rebuild()));

    /// <summary>Textura de 64×64 (BGRA) ya normalizada.</summary>
    public SkinComposer.Pixels? Skin
    {
        get => (SkinComposer.Pixels?)GetValue(SkinProperty);
        set => SetValue(SkinProperty, value);
    }

    /// <summary>Modelo delgado: brazos de 3 píxeles.</summary>
    public bool Slim
    {
        get => (bool)GetValue(SlimProperty);
        set => SetValue(SlimProperty, value);
    }

    private const double IdleDegreesPerSecond = 24;
    private static readonly TimeSpan IdleResumeDelay = TimeSpan.FromSeconds(2.5);

    private readonly ModelVisual3D _visual = new();
    private readonly AxisAngleRotation3D _rotation = new(new Vector3D(0, 1, 0), -28);
    private Point? _dragStart;
    private double _dragAngle;
    private DateTime _lastInteraction = DateTime.MinValue;
    private TimeSpan _lastFrame;

    public SkinViewer3D()
    {
        Background = Brushes.Transparent; // para recibir el ratón en toda la superficie
        Cursor = Cursors.SizeWE;
        ClipToBounds = true;

        var viewport = new Viewport3D
        {
            IsHitTestVisible = false,
            Camera = new PerspectiveCamera(new Point3D(0, 19, 76), new Vector3D(0, -3, -76), new Vector3D(0, 1, 0), 30),
        };
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0xB8, 0xB8, 0xB8)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x60, 0x60, 0x60), new Vector3D(-0.35, -0.55, -1)));
        viewport.Children.Add(new ModelVisual3D { Content = lights });
        _visual.Transform = new RotateTransform3D(_rotation);
        viewport.Children.Add(_visual);
        Children.Add(viewport);

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        LostMouseCapture += (_, _) => _dragStart = null;
        IsVisibleChanged += (_, e) =>
        {
            // Solo se anima mientras se ve: así no consume nada con el panel cerrado.
            CompositionTarget.Rendering -= OnRendering;
            if (e.NewValue is true)
                CompositionTarget.Rendering += OnRendering;
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    // ── Giro ────────────────────────────────────────────────────────────────

    private void OnRendering(object? sender, EventArgs e)
    {
        var time = ((RenderingEventArgs)e).RenderingTime;
        var elapsed = _lastFrame == TimeSpan.Zero ? 0 : (time - _lastFrame).TotalSeconds;
        _lastFrame = time;
        if (_dragStart is null && DateTime.UtcNow - _lastInteraction > IdleResumeDelay && elapsed is > 0 and < 0.25)
            _rotation.Angle = (_rotation.Angle + IdleDegreesPerSecond * elapsed) % 360;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragAngle = _rotation.Angle;
        CaptureMouse();
        e.Handled = true; // que no arrastre la ventana
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start)
            return;
        _rotation.Angle = _dragAngle + (e.GetPosition(this).X - start.X) * 0.8;
        _lastInteraction = DateTime.UtcNow;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _lastInteraction = DateTime.UtcNow;
        ReleaseMouseCapture();
    }

    // ── Modelo ──────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        if (Skin is not { Width: 64, Height: 64 } skin)
        {
            _visual.Content = null;
            return;
        }

        var material = new DiffuseMaterial(CreateBrush(skin));
        var inner = new MeshGeometry3D();
        var outer = new MeshGeometry3D();
        var arm = Slim ? 3 : 4;

        // Caja: posición mínima (x, y, z), tamaño (ancho, alto, fondo), origen en la textura y si es capa exterior.
        // El jugador mira hacia +Z; su lado derecho queda a la izquierda de la pantalla (-X).
        AddBox(inner, -4, 24, -4, 8, 8, 8, 0, 0);                // cabeza
        AddBox(outer, -4, 24, -4, 8, 8, 8, 32, 0, 0.5);          // sombrero
        AddBox(inner, -4, 12, -2, 8, 12, 4, 16, 16);              // cuerpo
        AddBox(outer, -4, 12, -2, 8, 12, 4, 16, 32, 0.25);        // chaqueta
        AddBox(inner, -4 - arm, 12, -2, arm, 12, 4, 40, 16);      // brazo derecho
        AddBox(outer, -4 - arm, 12, -2, arm, 12, 4, 40, 32, 0.25);
        AddBox(inner, 4, 12, -2, arm, 12, 4, 32, 48);             // brazo izquierdo
        AddBox(outer, 4, 12, -2, arm, 12, 4, 48, 48, 0.25);
        AddBox(inner, -4, 0, -2, 4, 12, 4, 0, 16);                // pierna derecha
        AddBox(outer, -4, 0, -2, 4, 12, 4, 0, 32, 0.25);
        AddBox(inner, 0, 0, -2, 4, 12, 4, 16, 48);                // pierna izquierda
        AddBox(outer, 0, 0, -2, 4, 12, 4, 0, 48, 0.25);

        var group = new Model3DGroup();
        group.Children.Add(new GeometryModel3D(inner, material));
        group.Children.Add(new GeometryModel3D(outer, material)); // después: su transparencia deja ver la base
        group.Freeze();
        _visual.Content = group;
    }

    /// <summary>
    /// La textura se amplía ×8 sin suavizar: así se ve pixelada como en el juego y el filtrado de la
    /// tarjeta gráfica no mezcla los bordes de caras vecinas.
    /// </summary>
    private static ImageBrush CreateBrush(SkinComposer.Pixels skin)
    {
        const int scale = 8;
        var size = 64 * scale;
        var data = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
                Buffer.BlockCopy(skin.Bgra, ((y / scale) * 64 + x / scale) * 4, data, (y * size + x) * 4, 4);
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, data, size * 4);
        bitmap.Freeze();
        // Coordenadas de textura absolutas (0..1 = la imagen entera), no relativas a cada malla.
        var brush = new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        brush.Freeze();
        return brush;
    }

    private static void AddBox(MeshGeometry3D mesh, double x, double y, double z, int w, int h, int d, int u, int v, double inflate = 0)
    {
        double x0 = x - inflate, y0 = y - inflate, z0 = z - inflate;
        double x1 = x + w + inflate, y1 = y + h + inflate, z1 = z + d + inflate;

        // Cada cara: esquinas vistas desde fuera (arriba-izq., arriba-der., abajo-der., abajo-izq.) y su rectángulo en la textura.
        AddFace(mesh, new(x0, y1, z1), new(x1, y1, z1), new(x1, y0, z1), new(x0, y0, z1), u + d, v + d, w, h);                 // frente
        AddFace(mesh, new(x1, y1, z0), new(x0, y1, z0), new(x0, y0, z0), new(x1, y0, z0), u + d + w + d, v + d, w, h);         // espalda
        AddFace(mesh, new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), new(x0, y0, z0), u, v + d, d, h);                     // lado derecho
        AddFace(mesh, new(x1, y1, z1), new(x1, y1, z0), new(x1, y0, z0), new(x1, y0, z1), u + d + w, v + d, d, h);             // lado izquierdo
        AddFace(mesh, new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1), u + d, v, w, d);                     // arriba
        AddFace(mesh, new(x0, y0, z1), new(x1, y0, z1), new(x1, y0, z0), new(x0, y0, z0), u + d + w, v, w, d);                 // abajo
    }

    private static void AddFace(MeshGeometry3D mesh, Point3D topLeft, Point3D topRight, Point3D bottomRight, Point3D bottomLeft,
        int u, int v, int width, int height)
    {
        // Un poco hacia dentro del rectángulo para no tomar píxeles de la cara de al lado.
        const double inset = 0.02;
        double u0 = (u + inset) / 64, v0 = (v + inset) / 64, u1 = (u + width - inset) / 64, v1 = (v + height - inset) / 64;

        var start = mesh.Positions.Count;
        mesh.Positions.Add(topLeft);
        mesh.Positions.Add(topRight);
        mesh.Positions.Add(bottomRight);
        mesh.Positions.Add(bottomLeft);
        mesh.TextureCoordinates.Add(new Point(u0, v0));
        mesh.TextureCoordinates.Add(new Point(u1, v0));
        mesh.TextureCoordinates.Add(new Point(u1, v1));
        mesh.TextureCoordinates.Add(new Point(u0, v1));
        // Sentido antihorario visto desde fuera: solo se dibuja la cara exterior.
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 3);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start + 1);
    }
}
